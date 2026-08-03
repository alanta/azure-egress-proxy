using System.Net;
using ControlPlane.Model;
using ControlPlane.Policy;
using ControlPlane.Storage;
using Polly;
using Polly.Registry;

namespace ControlPlane.Rulesets;

/// <summary>What a caller asked for. <see cref="Acl"/> is settable only at onboard. <see cref="Subjects"/>
/// is set at onboard; on an existing ruleset, changing it is a membership change that needs the
/// <c>bind</c> verb — restating the stored subjects unchanged is always fine.</summary>
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
    SubjectDiff? SubjectDiff = null,
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

    /// <summary>
    /// Publishing the rendered document is a second write, so it can fail on its own after the
    /// state write has already committed. It gets its own bounded retry because that failure is
    /// almost always transient — and because the alternative, a silently stale proxy, is the one
    /// outcome this system must not produce quietly.
    /// </summary>
    public const string PublishPipeline = "allowlist-publish";

    public async Task<StateDocument> ReadStateAsync(CancellationToken cancellationToken) =>
        (await store.ReadAsync(cancellationToken)).State;

    /// <summary>The state plus the recency the read endpoints stamp onto their responses.</summary>
    public Task<StateSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        store.ReadAsync(cancellationToken);

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

        List<Subject> subjects;
        if (isOnboard)
        {
            subjects = request.Subjects ?? [];
        }
        else
        {
            // Acl stays write-once at onboard; only membership has a verb to unlock it.
            if (RejectsAclChange(request, existing!) is { } aclError)
            {
                return (new WriteOutcome(aclError), null);
            }

            // A change to which workloads the ruleset governs is the sensitive half of the write,
            // distinct from editing hosts: it needs `bind`, which the onboarding owner holds via
            // trust-on-first-use. A restated (unchanged) subject list needs nothing, so a
            // desired-state pipeline that only holds `update` keeps pushing its one file.
            if (request.Subjects is { } requested && RulesetPolicy.MembershipChanges(existing!.Subjects, requested))
            {
                if (!RulesetPolicy.IsAuthorized(state, caller, Verbs.Bind, name, existing))
                {
                    return (Forbidden(
                        $"changing the subjects of ruleset '{name}' requires the 'bind' verb; "
                        + "an update writes allowed_hosts and action only"), null);
                }

                subjects = requested;
            }
            else
            {
                subjects = existing!.Subjects;
            }
        }

        // Membership (onboard or a bind) is validated and uniqueness-checked; a restated list is
        // already-stored and needs neither. CheckSubjectsAreUnclaimed excludes this ruleset by
        // name, so keeping existing subjects while adding one only tests the newcomer.
        if (isOnboard || !ReferenceEquals(subjects, existing!.Subjects))
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

        if (RulesetPolicy.CheckWriterIsNotSubject(state, caller, subjects) is { } writerError)
        {
            return (new WriteOutcome(writerError), null);
        }

        var diff = RulesetPolicy.Diff(existing?.Content.AllowedHosts ?? [], content.AllowedHosts);
        var subjectDiff = isOnboard ? null : RulesetPolicy.DiffSubjects(existing!.Subjects, subjects);

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

        return (new WriteOutcome(Ruleset: written, Diff: diff, SubjectDiff: subjectDiff, Created: isOnboard), next);
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
    /// Acl is write-once at onboard: it is reserved for the management portal (Mode 3) and the Mode 2
    /// write path never edits it. Subjects are handled separately — a change there is gated by the
    /// <c>bind</c> verb rather than refused outright. Restating the stored acl unchanged is fine, so a
    /// desired-state pipeline that echoes what it read is not tripped up.
    /// </summary>
    private static PolicyError? RejectsAclChange(PushRequest request, Ruleset existing)
    {
        if (request.Acl is { } acl && !SameAcl(acl, existing.Acl))
        {
            return new PolicyError(HttpStatusCode.BadRequest,
                "'acl' is set at onboard and frozen afterwards; an update writes allowed_hosts and action only");
        }

        return null;
    }

    /// <summary>
    /// Compares acls by value. Record equality would compare the underlying lists by reference, so
    /// a pipeline restating the acl it just read — different list instances, identical content —
    /// would be refused as a change. Order is not significant: these are identity sets.
    /// </summary>
    private static bool SameAcl(Acl? left, Acl? right)
    {
        left ??= new Acl();
        right ??= new Acl();

        return SameIdentities(left.Edit, right.Edit)
            && SameIdentities(left.Push, right.Push)
            && SameIdentities(left.Admin, right.Admin);
    }

    private static bool SameIdentities(List<string> left, List<string> right) =>
        left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

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

                // The state write above is the linearization point: past it the mutation is
                // durable whatever happens next, so a publish failure cannot be reported as if
                // nothing had been written.
                try
                {
                    await pipelines.GetPipeline(PublishPipeline).ExecuteAsync(
                        async publishToken => await store.PublishAllowlistAsync(
                            AllowlistRenderer.Render(next), publishToken),
                        token);
                }
                catch (Exception e)
                {
                    audit.PublishFailed(caller, outcome, e);
                    logger.LogError(e,
                        "ruleset {Ruleset} was committed but the rendered allowlist could not be published; "
                        + "the proxy keeps serving the previous configuration until a later write republishes it",
                        outcome.Ruleset?.Name);

                    return outcome with
                    {
                        Error = new PolicyError(HttpStatusCode.ServiceUnavailable,
                            $"ruleset '{outcome.Ruleset?.Name}' was saved, but publishing it to the proxy failed. "
                            + "The proxy is still serving the previous configuration. Retry this request: it is a "
                            + "full replace, so repeating it is safe and will republish."),
                    };
                }

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
