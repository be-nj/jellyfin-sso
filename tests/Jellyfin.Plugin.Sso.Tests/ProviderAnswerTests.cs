using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// How a call to the provider can fail, and what every caller sees of it. Each caller
/// answers an InvalidOperationException with a refusal in words; anything else reached
/// Jellyfin's exception middleware as a 500. The end-to-end tests
/// <c>A_discovery_document_that_trickles_in_is_given_up_on_and_refused_in_words</c> (#321)
/// and <c>A_provider_answer_of_many_megabytes_is_refused</c> (#322) drive the real client.
/// </summary>
public sealed class ProviderAnswerTests
{
    [Fact]
    public async Task The_clients_own_timeout_becomes_a_refusal()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OidcClientFactory.Answered<int>(
                () => throw new TaskCanceledException("timed out", new TimeoutException()),
                "discovery",
                CancellationToken.None));

        Assert.Contains("did not finish answering discovery", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_request_stays_a_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OidcClientFactory.Answered<int>(
                () => throw new OperationCanceledException(cancelled.Token),
                "discovery",
                cancelled.Token));
    }

    [Fact]
    public async Task An_answer_too_large_to_buffer_becomes_a_refusal()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OidcClientFactory.Answered<int>(
                () => throw new HttpRequestException(
                    "Cannot write more bytes to the buffer than the configured maximum buffer size: 1048576."),
                "the userinfo request",
                CancellationToken.None));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task What_the_provider_says_cannot_write_a_log_line_of_its_own()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OidcClientFactory.Answered<int>(
                () => throw new HttpRequestException("bad\nINFO forged line"),
                "the token request",
                CancellationToken.None));

        Assert.DoesNotContain('\n', ex.Message);
    }

    [Fact]
    public void The_cap_leaves_room_for_a_large_group_list_and_not_for_much_more()
    {
        Assert.InRange(OidcClientFactory.MaxResponseBytes, 512 * 1024, 4 * 1024 * 1024);
    }
}
