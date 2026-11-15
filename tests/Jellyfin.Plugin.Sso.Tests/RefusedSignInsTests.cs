using Jellyfin.Plugin.Sso.Provisioning;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The list of refused sign-ins is filled by people the server turned away — at an open
/// provider, by anybody — and read by an administrator who is about to decide whose
/// account an identity reaches. So what matters is that it stays small whatever arrives,
/// forgets, and carries nothing but a label next to the subject.
/// </summary>
public sealed class RefusedSignInsTests
{
    private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private RefusedSignIns Store() => new(() => _now);

    [Fact]
    public void A_refusal_is_listed_with_what_an_administrator_needs_to_link_it()
    {
        var store = Store();

        store.Remember("google", "1234567890", "Mae Jemison", RefusedSignInReason.NoAccount);

        var entry = Assert.Single(store.Recent());
        Assert.Equal(("google", "1234567890", "Mae Jemison"), (entry.ProviderId, entry.Sub, entry.Name));
        Assert.Equal(RefusedSignInReason.NoAccount, entry.Reason);
        Assert.Equal(_now, entry.At);
    }

    /// <summary>Somebody trying five times is one line, at the top, with the latest time.</summary>
    [Fact]
    public void The_same_identity_trying_again_is_one_entry_moved_to_the_top()
    {
        var store = Store();
        store.Remember("p", "first", "a", RefusedSignInReason.NoAccount);
        store.Remember("p", "second", "b", RefusedSignInReason.NoAccount);
        _now = _now.AddMinutes(5);

        store.Remember("p", "first", "a", RefusedSignInReason.NameTaken);

        Assert.Equal(["first", "second"], store.Recent().Select(e => e.Sub));
        Assert.Equal(_now, store.Recent()[0].At);
        Assert.Equal(RefusedSignInReason.NameTaken, store.Recent()[0].Reason);
    }

    /// <summary>The same subject at another provider is another person.</summary>
    [Fact]
    public void A_subject_is_only_the_same_identity_at_the_same_provider()
    {
        var store = Store();

        store.Remember("p1", "sub", "a", RefusedSignInReason.NoAccount);
        store.Remember("p2", "sub", "a", RefusedSignInReason.NoAccount);

        Assert.Equal(2, store.Recent().Count);
    }

    /// <summary>
    /// The cap. A provider that admits everybody lets everybody add a line, and the list
    /// must cost the same after ten thousand strangers as after fifty.
    /// </summary>
    [Fact]
    public void The_list_never_grows_past_its_capacity_and_keeps_the_newest()
    {
        var store = Store();

        for (var i = 0; i < RefusedSignIns.Capacity + 25; i++)
            store.Remember("p", $"sub-{i}", "x", RefusedSignInReason.NoAccount);

        var recent = store.Recent();
        Assert.Equal(RefusedSignIns.Capacity, recent.Count);
        Assert.Equal($"sub-{RefusedSignIns.Capacity + 24}", recent[0].Sub);
        Assert.DoesNotContain(recent, e => e.Sub == "sub-0");
    }

    [Fact]
    public void An_entry_older_than_a_day_is_gone()
    {
        var store = Store();
        store.Remember("p", "old", "x", RefusedSignInReason.NoAccount);
        _now = _now.Add(RefusedSignIns.Lifetime).AddSeconds(-1);
        store.Remember("p", "new", "x", RefusedSignInReason.NoAccount);
        Assert.Equal(2, store.Recent().Count);

        _now = _now.AddSeconds(2);

        Assert.Equal(["new"], store.Recent().Select(e => e.Sub));
    }

    [Fact]
    public void A_linked_identity_is_forgotten_and_the_list_can_be_cleared()
    {
        var store = Store();
        store.Remember("p", "a", "x", RefusedSignInReason.NoAccount);
        store.Remember("p", "b", "x", RefusedSignInReason.NoAccount);

        store.Forget("p", "a");
        Assert.Equal(["b"], store.Recent().Select(e => e.Sub));

        store.Clear();
        Assert.Empty(store.Recent());
    }

    /// <summary>
    /// The name is a label out of somebody else's token: one line, and short. A subject
    /// cannot be shortened and still be linked, so one too long to be a subject is not
    /// listed at all, and neither is an empty one.
    /// </summary>
    [Fact]
    public void A_name_is_cut_to_a_label_and_an_unusable_subject_is_not_listed()
    {
        var store = Store();

        store.Remember("p", "ok", "line one\nline two" + new string('x', 200), RefusedSignInReason.NoAccount);
        store.Remember("p", new string('s', RefusedSignIns.MaxSubjectLength + 1), "x", RefusedSignInReason.NoAccount);
        store.Remember("p", " ", "x", RefusedSignInReason.NoAccount);
        store.Remember("p", "nameless", null, RefusedSignInReason.NoAccount);

        var recent = store.Recent();
        Assert.Equal(["nameless", "ok"], recent.Select(e => e.Sub));
        var label = recent[1].Name;
        Assert.DoesNotContain('\n', label);
        Assert.Equal(RefusedSignIns.MaxNameLength + 1, label.Length);
        Assert.Equal(string.Empty, recent[0].Name);
    }
}
