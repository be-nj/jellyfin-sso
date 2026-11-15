using Jellyfin.Plugin.Sso.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>Why a sign-in the provider vouched for did not get an account here.</summary>
public enum RefusedSignInReason
{
    /// <summary>The provider is set not to create accounts at first login.</summary>
    NoAccount,

    /// <summary>A Jellyfin account of that name exists, and names are never matched.</summary>
    NameTaken,
}

/// <summary>One refused sign-in, as the Account Links tab shows it.</summary>
public sealed record RefusedSignIn(
    string ProviderId, string Sub, string Name, RefusedSignInReason Reason, DateTimeOffset At);

/// <summary>
/// The sign-ins this server recently turned away for want of a link, so an administrator
/// can link one without digging its subject out of the log.
///
/// In memory, a few dozen entries, a day at most. It is a convenience and is sized like
/// one: at a provider that admits everybody, anybody can put an entry here, so the list
/// has a hard cap and old entries make way for new ones. What an entry carries beyond
/// the subject is a name the provider sent — at some providers the person picks it — and
/// it is kept as a label and nothing else. In particular it never suggests which account
/// to link: matching an identity to an account by name is the takeover this plugin
/// refuses to perform, and a pre-filled suggestion would only move that mistake from
/// the code to the administrator's click.
/// </summary>
public sealed class RefusedSignIns
{
    internal const int Capacity = 50;
    internal const int MaxSubjectLength = 255;
    internal const int MaxNameLength = 64;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly List<RefusedSignIn> _entries = [];
    private readonly Func<DateTimeOffset> _clock;

    public RefusedSignIns()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    internal RefusedSignIns(Func<DateTimeOffset> clock) => _clock = clock;

    /// <summary>
    /// Records a refusal. A subject too long to be one is not recorded at all — it could
    /// not be shortened and still be linked — and a name is cut to a label's length.
    /// </summary>
    public void Remember(string providerId, string sub, string? name, RefusedSignInReason reason)
    {
        if (string.IsNullOrWhiteSpace(sub) || sub.Length > MaxSubjectLength)
            return;

        var label = Loggable.OneLine(name);
        if (label.Length > MaxNameLength)
            label = label[..MaxNameLength] + "…";

        var entry = new RefusedSignIn(providerId, sub, label, reason, _clock());
        lock (_gate)
        {
            _entries.RemoveAll(e => Same(e, providerId, sub));
            _entries.Insert(0, entry);
            if (_entries.Count > Capacity)
                _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
    }

    /// <summary>Drops an identity from the list, once it has been linked.</summary>
    public void Forget(string providerId, string sub)
    {
        lock (_gate)
            _entries.RemoveAll(e => Same(e, providerId, sub));
    }

    /// <summary>Empties the list.</summary>
    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    /// <summary>The refusals that are still recent, newest first.</summary>
    public IReadOnlyList<RefusedSignIn> Recent()
    {
        var oldest = _clock() - Lifetime;
        lock (_gate)
        {
            _entries.RemoveAll(e => e.At < oldest);
            return _entries.ToList();
        }
    }

    private static bool Same(RefusedSignIn entry, string providerId, string sub)
        => string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(entry.Sub, sub, StringComparison.Ordinal);
}
