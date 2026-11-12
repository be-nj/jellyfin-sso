namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Another request changed the account this login was about to create or link, while it
/// was waiting on the database. Nothing is misconfigured and nothing was granted; the
/// person only has to sign in again. Kept apart from InvalidOperationException, which the
/// callback reports as a configuration problem and sends the operator looking for one.
/// </summary>
public sealed class LoginRaceException(string message, Exception? inner = null)
    : Exception(message, inner);
