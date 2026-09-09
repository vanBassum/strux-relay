namespace StruxRelay.Models;

/// <summary>
/// Why a device has no UI manifest, when it has none.
///
/// The distinction that matters is the first two. <see cref="Absent"/> is a device
/// whose firmware has no <c>ui modules</c> command — the mixed-fleet case, which is
/// the common one and is not a fault: the shell falls back to the device's own page.
/// <see cref="Error"/> is a device that should have answered and did not, which IS
/// worth showing. Collapsing them would make every old board look broken and every
/// broken board look old.
/// </summary>
internal enum UiManifestStatus
{
    /// <summary>The device answered with a manifest.</summary>
    Ready,

    /// <summary>No pipe, so nothing to ask over.</summary>
    Offline,

    /// <summary>The device refused the command. It ships no modules.</summary>
    Absent,

    /// <summary>It should have answered. It did not.</summary>
    Error,
}

/// <summary>
/// One device's UI manifest, as the dashboard's shell asks for it.
///
/// <paramref name="Manifest"/> is the device's own reply text, unparsed. The relay
/// has no business understanding it — the shape belongs to the shell contract, which
/// lives in the frontend and is versioned by <c>hostApi</c> — and re-serialising it
/// here would mean two places that have to agree about it. Only the cache warmer
/// looks inside, and only for the one field it needs.
///
/// It is deliberately NOT cached. The nav is drawn from this, and a stale nav is a
/// sidebar full of pages that then fail; the file cache exists to save round trips on
/// bytes that carry their own version in their name, which a command reply does not.
/// </summary>
internal sealed record DeviceUiView(
    UiManifestStatus Status,
    string? Manifest = null,
    string? Detail = null);

/// <summary>
/// What one command on a device came back with.
///
/// A result rather than an exception, and the reason is specific: SignalR does not
/// hand a HubException's message to the caller intact. It prepends "An unexpected
/// error occurred invoking 'X' on the server." and appends the type name, so a
/// browser asking for "led get" on a device that refuses gets a sentence about the
/// SERVER having a problem with the DEVICE's own error buried in it. The module
/// contract promises `request` rejects with the device's own reason, and the only
/// way to keep that promise is to carry the reason as data and throw on the far side.
///
/// <paramref name="Refused"/> separates the device declining — an unknown command, a
/// handler's own error — from the pipe failing. Nothing consumes it yet; it is here
/// because the flag exists on the wire and reconstructing it from message text later
/// is exactly the guessing this type removes.
/// </summary>
internal sealed record DeviceCommandResult(
    bool Ok,
    string? Reply = null,
    string? Error = null,
    bool Refused = false);
