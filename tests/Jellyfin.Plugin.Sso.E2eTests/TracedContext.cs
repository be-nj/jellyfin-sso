using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A browser context that records a Playwright trace for the test that opened it.
///
/// Two failures in one day were visible only in a full run and not when their class ran
/// alone, and both took an extra round trip through CI — add a diagnostic, push, wait,
/// read — to find out what the browser had actually done. A trace answers that without the
/// round trip: every action, the DOM before and after it, the console and the network, in
/// a viewer.
///
/// The trace is always written, to tests/runs/playwright, which is gitignored and which CI
/// uploads with the other artifacts when a job fails. Always rather than on failure
/// because xUnit does not tell a test whether it passed, and a few hundred kilobytes per
/// browser test is a cheaper price than a second CI run to find out.
///
/// The name comes from the calling test method, so a site that opens a context needs no
/// name of its own:
///
/// <code>
/// await using var traced = await TracedContext.StartAsync(_browser, new() { ... });
/// var page = await traced.NewPageAsync();
/// </code>
/// </summary>
internal sealed class TracedContext : IAsyncDisposable
{
    private readonly string _name;

    private TracedContext(IBrowserContext context, string name)
    {
        Context = context;
        _name = name;
    }

    internal IBrowserContext Context { get; }

    internal static async Task<TracedContext> StartAsync(
        IBrowser browser,
        BrowserNewContextOptions? options = null,
        [CallerMemberName] string name = "context")
    {
        var context = await browser.NewContextAsync(options);

        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Title = name,
            Screenshots = true,
            Snapshots = true,
            // The C# sources add weight and say nothing the stack trace does not.
            Sources = false,
        });

        // Every page in this context has its console recorded, so a wait that gives up
        // can say what the client was doing. On the context's own event rather than in
        // NewPageAsync below, because half the call sites open their page straight off
        // Context and a page opened by the application itself has no call site at all.
        context.Page += (_, page) => PageConsole.Record(page);

        return new TracedContext(context, name);
    }

    internal Task<IPage> NewPageAsync() => Context.NewPageAsync();

    public async ValueTask DisposeAsync()
    {
        var directory = Path.Combine(BrowserSupport.RunsDirectory(), "playwright");
        Directory.CreateDirectory(directory);

        try
        {
            await Context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = Path.Combine(directory, $"{_name}.trace.zip"),
            });
        }
        catch (PlaywrightException)
        {
            // A context the test already closed, or a browser that went away with it.
            // Losing the trace of a test must not turn into a second failure on top of
            // whatever the test was reporting.
        }

        await Context.DisposeAsync();
    }
}
