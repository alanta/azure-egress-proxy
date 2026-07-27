using ControlPlane.Model;

namespace ControlPlane.Rulesets;

/// <summary>
/// The audit trail for writes. It carries real weight in this design: because a push is a full
/// replace and nothing coerces a host added to an already-enforcing ruleset, these events plus the
/// <c>:check</c> diff are the only controls over both widening and removal. They are structured so
/// a log query can alert on them.
/// </summary>
public sealed class AuditLog(ILogger<AuditLog> logger)
{
    public void Written(string caller, string verb, WriteOutcome outcome)
    {
        var ruleset = outcome.Ruleset;
        if (ruleset is null)
        {
            return;
        }

        logger.LogInformation(
            "audit: ruleset {Ruleset} {Verb} by {Identity}; action={Action} hosts={HostCount} subjects={Subjects}",
            ruleset.Name,
            verb,
            caller,
            ruleset.Content.Action,
            ruleset.Content.AllowedHosts.Count,
            string.Join(",", ruleset.Subjects.Select(s => s.ToString())));

        foreach (var host in outcome.Diff?.Added ?? [])
        {
            logger.LogInformation(
                "audit: host {Host} ADDED to ruleset {Ruleset} by {Identity} (action={Action})",
                host, ruleset.Name, caller, ruleset.Content.Action);
        }

        foreach (var host in outcome.Diff?.Removed ?? [])
        {
            logger.LogInformation(
                "audit: host {Host} REMOVED from ruleset {Ruleset} by {Identity}",
                host, ruleset.Name, caller);
        }
    }
}
