using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Interfaces;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

public sealed class SsoUserPersistenceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly IDbContextFactory<JellyfinDbContext> _contexts = Substitute.For<IDbContextFactory<JellyfinDbContext>>();
    private SsoUserPersistence _persistence = null!;
    private Func<Task>? _beforeSave;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _contexts.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Context()));
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        _persistence = new SsoUserPersistence(_contexts, Substitute.For<IEventManager>(),
            NullLogger<SsoUserPersistence>.Instance);
    }

    private JellyfinDbContext Context()
    {
        var locking = Substitute.For<IEntityFrameworkCoreLockingBehavior>();
        locking.OnSaveChangesAsync(Arg.Any<JellyfinDbContext>(), Arg.Any<Func<Task>>())
            .Returns(async call =>
            {
                var before = _beforeSave;
                _beforeSave = null;
                if (before is not null) await before();
                await call.Arg<Func<Task>>()();
            });
        return new JellyfinDbContext(
            new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options,
            NullLogger<JellyfinDbContext>.Instance, Substitute.For<IJellyfinDatabaseProvider>(), locking);
    }

    [Fact]
    public async Task The_first_persisted_account_is_disabled_sso_only_and_has_no_library_access()
    {
        var created = await _persistence.CreateAsync("new-user", default);
        await using var db = Context();
        var stored = await db.Users.Include(u => u.Permissions).SingleAsync(u => u.Id == created.Id);
        Assert.Equal(typeof(SsoAuthenticationProvider).FullName, stored.AuthenticationProviderId);
        Assert.True(stored.HasPermission(PermissionKind.IsDisabled));
        Assert.False(stored.HasPermission(PermissionKind.IsAdministrator));
        Assert.False(stored.HasPermission(PermissionKind.EnableAllFolders));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_image_write_preserves_account_permissions_and_authentication(bool existingImage)
    {
        var user = await _persistence.CreateAsync("avatar-user", default);
        if (existingImage)
            await _persistence.SetProfileImageAsync(user.Id, "/old.png", default);
        await using (var initial = Context())
        {
            var stored = await initial.Users.Include(u => u.Permissions).SingleAsync();
            stored.SetPermission(PermissionKind.IsDisabled, false);
            await initial.SaveChangesAsync();
        }

        // Change policy after the image writer has read the account, immediately before
        // its database save. Re-reading a snapshot before UpdateUserAsync would fail here.
        _beforeSave = async () =>
        {
            await using (var admin = Context())
            {
                var stored = await admin.Users.Include(u => u.Permissions).SingleAsync(u => u.Id == user.Id);
                stored.SetPermission(PermissionKind.IsDisabled, true);
                stored.AuthenticationProviderId = "changed-by-admin";
                await admin.SaveChangesAsync();
            }
        };

        await _persistence.SetProfileImageAsync(user.Id, "/new.png", default);

        await using var db = Context();
        var result = await db.Users.Include(u => u.Permissions).Include(u => u.ProfileImage).SingleAsync();
        Assert.True(result.HasPermission(PermissionKind.IsDisabled));
        Assert.Equal("changed-by-admin", result.AuthenticationProviderId);
        Assert.Equal("/new.png", result.ProfileImage!.Path);
    }

    [Fact]
    public async Task The_first_persisted_account_has_no_password()
    {
        var created = await _persistence.CreateAsync("no-password", default);
        await using var db = Context();
        var stored = await db.Users.SingleAsync(u => u.Id == created.Id);
        Assert.Null(stored.Password);
        Assert.Equal(SsoAuthenticationProvider.DefaultPasswordResetProviderId, stored.PasswordResetProviderId);
    }

    [Fact]
    public async Task A_name_taken_before_the_check_is_a_race_not_a_configuration_error()
    {
        await _persistence.CreateAsync("Taken", default);
        await Assert.ThrowsAsync<LoginRaceException>(() => _persistence.CreateAsync("taken", default));
    }

    [Fact]
    public async Task A_name_taken_between_the_check_and_the_write_is_a_race_too()
    {
        _beforeSave = async () =>
        {
            await using var other = Context();
            other.Users.Add(new User("racer", "local", "reset") { InternalId = 99 });
            await other.SaveChangesAsync();
        };

        await Assert.ThrowsAsync<LoginRaceException>(() => _persistence.CreateAsync("racer", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" padded")]
    [InlineData("..")]
    [InlineData("semi;colon")]
    public async Task A_name_jellyfin_would_refuse_is_refused_before_anything_is_written(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _persistence.CreateAsync(name, default));
        await using var db = Context();
        Assert.Empty(db.Users);
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();
}
