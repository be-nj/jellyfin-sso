using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// What a page has written to its console, kept for the moment a wait gives up.
///
/// A console message can only be caught as it arrives, and a wait that fails learns it
/// failed long afterwards — so by the time there is something to report, the messages that
/// explain it are gone unless somebody was listening. Five call sites used to attach
/// their own handler, each for one test, and the waits that mattered had none: the profile-page
/// wait in LoginFormButtonTests timed out on main with nothing but
/// `TimeoutException : Timeout 90000ms exceeded`, and finding out why meant downloading
/// the job's artifact and unzipping a Playwright trace to read three lines.
///
/// Every page a <see cref="TracedContext"/> opens is recorded here instead, so any failure
/// can say what the client was doing. The table holds weak references to the pages, so a
/// closed page takes its messages with it.
/// </summary>
internal static class PageConsole
{
    private static readonly ConditionalWeakTable<IPage, List<string>> Messages = new();

    /// <summary>Starts recording. Called for every page <see cref="TracedContext"/> opens.</summary>
    internal static void Record(IPage page)
    {
        var lines = Messages.GetOrCreateValue(page);

        page.Console += (_, message) =>
        {
            lock (lines)
            {
                lines.Add($"{message.Type}: {message.Text}");
            }
        };
    }

    /// <summary>Everything this page has logged, oldest first.</summary>
    internal static IReadOnlyList<string> Of(IPage page)
    {
        if (!Messages.TryGetValue(page, out var lines))
        {
            return [];
        }

        lock (lines)
        {
            return [.. lines];
        }
    }

    /// <summary>Only what the page logged as an error.</summary>
    internal static IReadOnlyList<string> ErrorsOf(IPage page)
        => [.. Of(page).Where(line => line.StartsWith("error:", StringComparison.Ordinal))];

    /// <summary>
    /// Did the web client itself fall over while rendering, rather than the page simply
    /// not being ready?
    ///
    /// jellyfin-web is a React application and its router catches an exception thrown
    /// during render, logs it and shows nothing. Nothing a test waits for will ever
    /// appear after that, so waiting the rest of the ninety seconds is wasted and the
    /// timeout that follows describes the symptom and not the cause. 12.2 did this on
    /// main while leaving the home tab for the profile view:
    ///
    /// <code>
    /// [userprofile] Unexpected null page reference
    /// TypeError: t.value.pause is not a function
    ///     at Object.pause (.../65126.…chunk.js:1:26254)
    ///     at n.value (.../hometab.…)
    /// React Router caught the following error during render TypeError: t.value.pause …
    /// </code>
    ///
    /// The first of those three lines is in every run of that test, on 12.0 and 12.1 that
    /// passed as well, so it is noise and not the signal. The router's own line is the
    /// signal, and it appeared on 12.2 alone.
    /// </summary>
    internal static bool ClientCrashedWhileRendering(IEnumerable<string> messages)
        => messages.Any(line =>
            line.Contains("caught the following error during render", StringComparison.Ordinal));
}
