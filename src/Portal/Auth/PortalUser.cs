namespace Portal.Auth;

/// <summary>
/// A signed-in operator. Deliberately thin: the portal serves ONE audience tier, so there is
/// nothing here to scope a view by. If a field appears on this record that filters what a user
/// may see, per-ruleset RBAC has been designed by accident — that is Mode 3's job, and this
/// change defers it. See docs/control-plane.md and the proposal's Non-Goals.
/// </summary>
/// <param name="Subject">Stable identifier from the identity provider. Audit-shaped, not a key
/// into any policy document.</param>
/// <param name="DisplayName">What to render in the header.</param>
public sealed record PortalUser(string Subject, string DisplayName);

/// <summary>
/// How the portal learns who is at the keyboard — and the ONLY place in the portal that knows.
///
/// This exists so the identity provider can change without touching anything else. The
/// control-plane API is a machine interface and calls out to no IdP; the portal calls it with its
/// own managed identity regardless of who signed in (see design.md D1/D2). Pass-through of a user
/// token was rejected precisely because it would pin the portal to Entra forever.
///
/// Implementations must be cheap and synchronous-ish: this is consulted on every request.
/// </summary>
public interface IPortalUserAccessor
{
    /// <summary>The signed-in user, or null when the request carries no usable session. Null is
    /// not an error here — the caller decides whether that means "redirect" or "401".</summary>
    PortalUser? Current { get; }
}
