using System.Net;
using ControlPlane.Model;
using ControlPlane.Policy;
using ControlPlane.Storage;
using Polly;
using Polly.Registry;

namespace ControlPlane.Rulesets;

/// <summary>What a caller asked for. <see cref="Subjects"/> and <see cref="Acl"/> are settable only
/// at onboard; on an existing ruleset they may only restate what is already stored.</summary>
public sealed record PushRequest
{
    public List<Subject>? Subjects { get; init; }
    public RulesetContent? Content { get; init; }
    public Acl? Acl { get; init; }
}

public sealed record WriteOutcome(
    PolicyError? Error = null,
    Ruleset? Ruleset = null,
    HostDiff? Diff = null,
    bool Created = false)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// The validating write path. Every mutation is a read-modify-write over the single state blob:
/// read fresh state + ETag, re-apply the whole transform, write under <c>If-Match</c>. The transform
/// is a pure function of the state it just read, so re-running it on a retry is safe — which is what
/// makes a losing race against a DIFFERENT ruleset resolve itself instead of surfacing to the caller.
/// </summary>
public sealed class RulesetService(
    IStateBlobStore store,
    ResiliencePipelineProvider<string> pipelines,
    ILogger<RulesetService> logger,
    AuditLog audit)
{
    public const string RmwPipeline = "state-rmw";

    public async Task<StateDocument> ReadStateAsync(CancellationToken cancellationToken) =>
        (await store.ReadAsync(cancellationToken)).State;

    /// <summary>
    /// Onboard if absent, full-replace of content if present. Full replace is desired-state: the
    /// team's repo holds the rules file, the pipeline pushes it, so a host absent from the push is
    /// removed — which is why removals are audited and surfaced by <c>:check</c>.
    /// </summary>
    public Task<WriteOutcome> PutAsync(
        string name,
        PushRequest request,
        string caller,
        bool dryRun,
        CancellationToken cancellationToken) =>
        ExecuteAsync(state => Put(state, name, request, caller), caller, Verbs.Update, dryRun, cancellationToken);

    /// <summary>Offboard: remove the ruleset and free its subjects, which fall to the fallback/deny
    /// block on the next proxy reload. Decommission is fail-closed by construction.</summary>
    public Task<WriteOutcome> DeleteAsync(string name, string caller, CancellationToken cancellationToken) =>
        ExecuteAsync(state => Delete(state, name, caller), caller, Verbs.Offboard, dryRun: false, cancellationToken);

    private static (WriteOutcome Outcome, StateDocument? Next) Put(
        StateDocument state,
        string name,
        PushRequest request,
        string caller)
    {
        if (RulesetPolicy.ValidateName(name) is { } nameError)
        {
            return (new WriteOutcome(nameError), null);
        }

        var existing = state.Find(name);
        var isOnboard = existing is null;
        var verb = isOnboard ? Verbs.Onboard : Verbs.Update;

        if (!RulesetPolicy.IsAuthorized(state, caller, verb, name, existing))
        {
            return (Forbidden($"identity '{caller}' does not hold '{verb}' on ruleset '{name}'"), null);
        }

        var content = request.Content ?? new RulesetContent();
        if (RulesetPolicy.ValidateContent(content) is { } contentError)
        {
            return (new WriteOutcome(contentError), null);
        }

        var subjects = isOnboard ? request.Subjects ?? [] : existing!.Subjects;

        if (isOnboard)
        {
            if (RulesetPolicy.ValidateSubjects(subjects) is { } subjectError)
            {
                return (new WriteOutcome(subjectError), null);
            }

            if (RulesetPolicy.CheckSubjectsAreUnclaimed(state, name, subjects) is { } uniqueError)
            {
                return (new WriteOutcome(uniqueError), null);
            }
        }
        else if (RejectsFrozenFields(request, existing!) is { } frozenError)
        {
            return (new WriteOutcome(frozenError), null);
        }

        if (RulesetPolicy.CheckWriterIsNotSubject(state, caller, subjects) is { } writerError)
        {
            return (new WriteOutcome(writerError), null);
        }

        var diff = RulesetPolicy.Diff(existing?.Content.AllowedHosts ?? [], content.AllowedHosts);

        var written = new Ruleset
        {
            Name = name,
            Subjects = [.. subjects],
            Content = new RulesetContent
            {
                AllowedHosts = [.. content.AllowedHosts],
                Action = RulesetPolicy.EffectiveAction(isOnboard, content.Action),
            },
            Acl = isOnboard ? request.Acl : existing!.Acl,
            // Trust-on-first-use: the creator becomes the owner and gains update/offboard on it,
            // so onboarding costs one platform grant rather than a ticket per ruleset. An existing
            // ruleset's owner is never reassigned by a write.
            Owner = isOnboard ? caller : existing!.Owner,
        };

        var rulesets = state.Rulesets.Where(r => r.Name != name).Append(written);

        // Grants and fallback are copied through untouched: they are platform-owned, and the write
        // path must never be able to widen the authority that authorized it.
        var next = state with { Rulesets = [.. rulesets] };

        return (new WriteOutcome(Ruleset: written, Diff: diff, Created: isOnboard), next);
    }

    private static (WriteOutcome Outcome, StateDocument? Next) Delete(StateDocument state, string name, string caller)
    {
        var existing = state.Find(name);
        if (existing is null)
        {
            return (new WriteOutcome(new PolicyError(HttpStatusCode.NotFound, $"ruleset '{name}' not found")), null);
        }

        if (!RulesetPolicy.IsAuthorized(state, caller, Verbs.Offboard, name, existing))
        {
            return (Forbidden($"identity '{caller}' does not hold '{Verbs.Offboard}' on ruleset '{name}'"), null);
        }

        if (RulesetPolicy.CheckWriterIsNotSubject(state, caller, existing.Subjects) is { } writerError)
        {
            return (new WriteOutcome(writerError), null);
        }

        var diff = RulesetPolicy.Diff(existing.Content.AllowedHosts, []);
        var next = state with { Rulesets = [.. state.Rulesets.Where(r => r.Name != name)] };

        return (new WriteOutcome(Ruleset: existing, Diff: diff), next);
    }

    /// <summary>
    /// Subjects and acl are write-once at onboard: that is what makes steady-state identity hijack
    /// impossible, since an <c>update</c> grant can never move a workload under different rules. A
    /// desired-state pipeline that keeps restating the stored subjects is fine — only a *change* is
    /// refused.
    /// </summary>
    private static PolicyError? RejectsFrozenFields(PushRequest request, Ruleset existing)
    {
        if (request.Subjects is { } subjects
            && !subjects.Select(s => s.Key).ToHashSet().SetEquals(existing.Subjects.Select(s => s.Key)))
        {
            return new PolicyError(HttpStatusCode.BadRequest,
                "'subjects' is set at onboard and frozen afterwards; an update writes allowed_hosts and action only");
        }

        if (request.Acl is { } acl && acl != existing.Acl)
        {
            return new PolicyError(HttpStatusCode.BadRequest,
                "'acl' is set at onboard and frozen afterwards; an update writes allowed_hosts and action only");
        }

        return null;
    }

    private async Task<WriteOutcome> ExecuteAsync(
        Func<StateDocument, (WriteOutcome Outcome, StateDocument? Next)> transform,
        string caller,
        string verb,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var pipeline = pipelines.GetPipeline(RmwPipeline);

        try
        {
            return await pipeline.ExecuteAsync(async token =>
            {
                var snapshot = await store.ReadAsync(token);
                var (outcome, next) = transform(snapshot.State);

                // A rejection is a property of the state we just read, so there is nothing to retry
                // and nothing to write. Dry-run stops here by design: `:check` must never write.
                if (!outcome.Succeeded || dryRun || next is null)
                {
                    return outcome;
                }

                await store.WriteAsync(next, snapshot.ETag, token);
                await store.PublishAllowlistAsync(AllowlistRenderer.Render(next), token);

                audit.Written(caller, outcome.Created ? Verbs.Onboard : verb, outcome);
                return outcome;
            }, cancellationToken);
        }
        catch (StatePreconditionFailedException e)
        {
            // The retry budget is gone: sustained contention on the same ruleset. The caller (a
            // pipeline) can retry — one blob means writes are serialized, not lost.
            logger.LogWarning(e, "read-modify-write exhausted its retry budget");
            return new WriteOutcome(new PolicyError(HttpStatusCode.Conflict,
                "the control-plane state is under contention; retry the request"));
        }
    }

    private static WriteOutcome Forbidden(string message) =>
        new(new PolicyError(HttpStatusCode.Forbidden, message));
}
