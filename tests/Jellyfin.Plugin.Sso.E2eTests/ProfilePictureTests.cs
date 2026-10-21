using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The profile picture against a real Jellyfin: the plugin fetches what the token points
/// at and hands it to Jellyfin's own image pipeline, so the only proof worth having is
/// Jellyfin serving the image back for that user.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class ProfilePictureTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public ProfilePictureTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public async Task DisposeAsync()
    {
        _env.Idp.NextPictureUrl = null;
        await _env.PatchConfigAsync(config => config["SyncProfilePictures"] = false);
    }

    [Fact]
    public async Task The_picture_the_provider_publishes_becomes_the_users_own()
    {
        await EnableAsync();
        _env.Idp.NextPictureUrl = _env.Idp.AvatarUrl;

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-pic", "picuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var user = await _env.FindUserAsync("picuser");
        Assert.NotNull(user);
        var tag = user!["PrimaryImageTag"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(tag), "Jellyfin has no primary image for the user.");

        // Jellyfin serves it back, which is more than the database row saying it exists.
        using var client = JellyfinEnvironment.TrustingClient();
        var image = await client.GetAsync(
            $"{_env.BaseUrl}/Users/{user["Id"]!.GetValue<string>()}/Images/Primary");
        image.EnsureSuccessStatusCode();
        Assert.Equal("image", image.Content.Headers.ContentType?.MediaType?.Split('/')[0]);
    }

    [Fact]
    public async Task Nothing_is_fetched_while_the_setting_is_off()
    {
        _env.Idp.NextPictureUrl = _env.Idp.AvatarUrl;
        var before = _env.Idp.AvatarsServed;

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-nopic", "nopicuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        Assert.Equal(before, _env.Idp.AvatarsServed);
        var user = await _env.FindUserAsync("nopicuser");
        Assert.Null(user!["PrimaryImageTag"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_provider_that_publishes_no_picture_leaves_the_account_alone()
    {
        await EnableAsync();
        _env.Idp.NextPictureUrl = null;

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-plain", "plainuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var user = await _env.FindUserAsync("plainuser");
        Assert.NotNull(user);
        Assert.Null(user!["PrimaryImageTag"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_picture_that_is_not_an_image_is_refused_and_the_login_still_works()
    {
        await EnableAsync();
        // The provider's discovery document is JSON, served happily and not a picture.
        _env.Idp.NextPictureUrl = $"{_env.Idp.ContainerAuthority}/.well-known/openid-configuration";

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-json", "jsonuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var user = await _env.FindUserAsync("jsonuser");
        Assert.NotNull(user);
        Assert.Null(user!["PrimaryImageTag"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_provider_that_cannot_serve_userinfo_still_signs_people_in()
    {
        await EnableAsync();
        // No picture in the ID token, so the plugin asks userinfo for one — and this
        // provider cannot answer. Before the feature existed this login worked; it has
        // to keep working.
        _env.Idp.NextPictureUrl = null;
        _env.Idp.UserInfoFails = true;

        try
        {
            var login = await _env.StartWebLoginAsync(new FakeUser("sub-nouserinfo", "nouserinfouser", ["media"]));
            Assert.True(login.Succeeded, login.Body);

            var user = await _env.FindUserAsync("nouserinfouser");
            Assert.NotNull(user);
            Assert.Null(user!["PrimaryImageTag"]?.GetValue<string>());
        }
        finally
        {
            _env.Idp.UserInfoFails = false;
        }
    }

    private Task EnableAsync()
        => _env.PatchConfigAsync(config => config["SyncProfilePictures"] = true);
}
