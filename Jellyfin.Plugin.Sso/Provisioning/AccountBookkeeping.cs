namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// One login at a time, for the part of a login that reads the server's accounts and then
/// changes them.
///
/// Three of this plugin's rules are a question followed by an answer: is this the last
/// administrator, has this identity been linked already, does this username exist. Each
/// was asked and then acted on, with nothing in between saying that no other login may run
/// in the meantime — so each held only as long as nobody logged in at the same moment as
/// somebody else. Two administrators signing in together could both be demoted, because
/// each saw the other as the one who would be left. That locks everybody out of the server.
///
/// <see cref="Plugin.ConfigurationLock"/> cannot do this job: the work spans awaits —
/// Jellyfin's user and policy writes are asynchronous — and a lock may not be held across
/// one. So this is a semaphore, and it is deliberately coarse. It serialises the
/// bookkeeping of concurrent logins, not the logins themselves: the provider round trips,
/// the token validation and the pages are all outside it. What is inside is a handful of
/// in-memory reads and two database writes, measured in milliseconds, and a household's
/// logins do not queue behind that.
///
/// Not reentrant. Nothing inside takes it twice — the callback calls the provisioner and
/// the permission sync one after another, not one from the other — and a second take would
/// deadlock rather than misbehave quietly, which is the right way round for a mistake like
/// that to show up.
/// </summary>
internal static class AccountBookkeeping
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Waits for the turn and gives back the thing that ends it.
    ///
    /// <code>
    /// using var turn = await AccountBookkeeping.EnterAsync(ct).ConfigureAwait(false);
    /// </code>
    /// </summary>
    internal static async Task<Turn> EnterAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        return default;
    }

    /// <summary>Ends the turn. A struct, so taking a turn allocates nothing.</summary>
    internal readonly struct Turn : IDisposable
    {
        public void Dispose() => Gate.Release();
    }
}
