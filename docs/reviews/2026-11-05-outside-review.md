# Outside review, 5 November 2026

[Documentation index](../README.md) · [Threat model](../threat-model.md) ·
[The hardening pass earlier the same day](2026-11-05-hardening.md)

The first review of this plugin by something that did not write it. GitHub Copilot was
pointed at `76159d3` and filed eight findings as issues, two of them marked P1. Every one
was checked against the code here before anything was changed, and all eight were real.

Reviewed at `76159d3`; fixes landed in `#207`.

That is the headline and it is worth stating plainly: a review from outside, on a plugin
that had been through five internal passes and a twelve-finding hardening review the same
morning, found eight more. Three of them were in code written that morning, one a direct
follow-up to a fix from it. The [hardening review](2026-11-05-hardening.md) ends by saying
that an outside look was the most valuable thing that could happen to this plugin. This is
what that was worth.

## What came out of it

| # | Finding | Severity | Issue |
|---|---|---|---|
| 1 | Concurrent syncs could demote the last two administrators | P1 | [#198](https://github.com/be-nj/jellyfin-sso/issues/198) |
| 2 | A failed unlink removed the identity lookup before revoking | P1 | [#203](https://github.com/be-nj/jellyfin-sso/issues/203) |
| 3 | The multi-provider Quick Connect picker built the wrong route | P2 | [#201](https://github.com/be-nj/jellyfin-sso/issues/201) |
| 4 | Back-channel logout did not refresh keys after a rotation | P2 | [#200](https://github.com/be-nj/jellyfin-sso/issues/200) |
| 5 | Concurrent link callbacks could write conflicting links | P2 | [#204](https://github.com/be-nj/jellyfin-sso/issues/204) |
| 6 | Remote-image bodies outlived the configured timeout | P2 | [#205](https://github.com/be-nj/jellyfin-sso/issues/205) |
| 7 | The configuration page's requests omitted the base path | P2 | [#199](https://github.com/be-nj/jellyfin-sso/issues/199) |
| 8 | The login handoff saved a server address without the base path | P2 | [#202](https://github.com/be-nj/jellyfin-sso/issues/202) |

## The three that break a promise

**The last administrator.** The threat model has said since October that "the last
administrator is never demoted", and the guard reads the server's accounts and then writes
a policy with nothing between the two. Two administrators signing in at the same moment
each saw the other as the one who would be left, and both were demoted — which locks
everybody out of the server. The guard held in every test because no test ever ran two
things at once.

Now in `AccountBookkeeping`: a semaphore that the permission sync and the provisioner take
for the whole of their work. It is deliberately coarse — logins queue through a handful of
in-memory reads and two database writes — and `Plugin.ConfigurationLock` cannot do the job
because the work spans awaits.

**Unlinking.** The same morning's review had added revocation to the administrator's unlink
([#188](https://github.com/be-nj/jellyfin-sso/issues/188)), which was the missing call. The
order was still wrong in both endpoints: the link was removed and then the sessions were
revoked, so a revocation that threw — or a client that closed the connection and cancelled
the request — left a session nothing could reach, because back-channel logout finds an
account through its link and nothing else. The retry answered "no link found".

Now in `Unlinking`, one rule for both endpoints: ask whether there is anything to unlink,
end the sessions, remove the links, end them again. Nothing is removed before a revocation
has succeeded, so a failure leaves a state the caller can retry, and the second revocation
covers a login that completed in between.

**One identity per account.** `SelfServiceLinks.Decide` was extracted that morning to make
the rule testable, and the extraction left the decision outside the lock that the write
takes. Two callbacks completing together both read "nothing holds this identity and nothing
holds this account". The administrator's own linking endpoint has always done its checks
inside that lock; the callback now does too.

## The one that had never worked

Quick Connect through the provider picker. The picker built
`/sso/oidc/start/{id}?code=…` — the ordinary login route, which does not read the code — so
on a server with two providers, choosing one signed the browser in and left the device
waiting for ever. Since 19 October, when the picker was added.

Three tests covered Quick Connect. Three covered more than one provider. The pair of them
was covered by none, and the one test that looked at the picker's link asserted the broken
address, so it froze the bug rather than finding it. That is the second time in one day
that a passing test turned out to be agreeing with the code instead of checking it; the
first was [#187](https://github.com/be-nj/jellyfin-sso/issues/187).

## The one that was measured rather than argued

The fetcher asks for headers only, so that a body nobody wants is never buffered, and
`HttpClient.Timeout` stops applying once they have arrived. Whether that is true was not a
thing to settle from memory, so `RemoteImageDeadlineTests` stands up a listener that sends
a content type, a length and then silence:

```
before:  the fetch was still waiting after 00:00:30.25   (limit: 400 ms)
after:   three tests, one second
```

Thirty seconds against a six-second limit, with a login waiting on it. The deadline is now
a linked token over the whole fetch, headers, redirects and body, and it stays
distinguishable from the caller's own cancellation: a caller that went away is rethrown,
while the deadline is answered with no picture, because a picture is never worth failing a
login over.

## What the test catalogue had to say

Asked whether better tests would have caught these, the answer divided cleanly, and the
division is more useful than the findings:

- **Four were a pair of capabilities the suite already had and never crossed.**
  `FakeIdentityProvider.RotateSigningKey` existed and was used by one test, on the login
  side — pointing it at the logout endpoint was two lines (#200). `AddSecondProviderAsync`
  existed and `QuickConnectTests` never called it (#201). The browser tests read
  `ManualAddress` back and never ran under a prefix (#202). `ConfigPageTests` drove the real
  page in a real browser and never under a prefix (#199).
- **Three needed a kind of test that did not exist.** Of 677 tests, none ran two things at
  once (#198, #204), and exactly one substitute in the whole unit suite was made to throw —
  on a read path, never a write (#203).
- **One needed a measurement.** Nothing in the suite asserted a deadline (#205).

So the cheapest lesson is the first: the suite tested each capability on its own and almost
no combination of two. The additions here close the four crossings, and add the three
missing kinds — a forced overlap, a failing writer, a stalled socket.

## Two tests that had to be written twice

Both are worth recording, because both passed at first and proved nothing.

The link-race test began as two tasks starting from a barrier, two hundred rounds. It
passed against the broken code every time: the window between deciding and writing is a few
instructions wide and two threads do not land in it. It now holds the configuration lock
itself, so that both callbacks reach their decision while it is held, which is the state
the bug needs. Against the old shape it fails; against the fix it passes.

The logout-rotation test failed on its first run — along with every other test in its class,
in one millisecond each, which is the shape of a build that did not run rather than a
behaviour that is wrong. The cause was the rig: `RotateSigningKey` appended a fixed
`-rotated` to the key id, so a second rotation in one test run published new key material
under a key id the plugin had already cached. That is not a rotation, it is the shape of a
forgery — the key is found and the signature does not check out — and it made the provider
unusable for everything that followed. Every rotation now gets its own key id, as a real
provider's does.

## Still missing, and said so

`ManualAddress` and the configuration page were first held only by unit tests on the
generated text and on the absence of a root-relative address — decisive for the defect, and
not the same evidence as running them. That gap was filed as
[#206](https://github.com/be-nj/jellyfin-sso/issues/206) rather than recorded as covered,
and it is now closed: `PrefixedProxy` puts nginx in front of the Jellyfin container,
publishing it under `/jellyfin` over TLS and stripping the prefix on the way through, and
`PrefixedDeploymentTests` drives a browser against it. Both tests fail against the code as
it was before this review and pass after it, which is the evidence the unit tests could not
give.

Two things that walk came out of building it. The first run was refused by the provider
with `redirect_uri is not registered: https://host.docker.internal/jellyfin/sso/...` — no
port. nginx's `$host` drops it where `$http_host` keeps it, and the plugin builds the
redirect URI it registers out of that header, so the proxy has to pass the one with the
port in it. A deployment on 443 would never have shown it. The second was a flake of the
ordinary kind: reading `localStorage` straight after the credentials appear lands in the
middle of the handoff's own navigation and dies with "execution context was destroyed". The
test now waits for the web client to have adopted the session first, which is both the
stronger assertion and the one that survives the navigation.

## Method, theirs and ours

Copilot's issues named a file and a line range, a reproduction, the change required and
acceptance criteria. Four of the eight said plainly how far the reproduction went — "this
was not a live-server reproduction", "reproduced deterministically with a barrier in the
substituted policy writer" — which is what made them quick to check rather than quick to
believe.

Every one was read against the code here before anything was written, and every fix was
then verified by removing it again: the admin race, the link race, the rotation retry and
the deadline each have a test that fails without their fix. Nothing was taken on the
report's word, and nothing in the report turned out to be wrong.
