# Test practices audit, 5 November 2026

[Documentation index](../README.md) · [The outside review](2026-11-05-outside-review.md) ·
[CONTRIBUTING](../../CONTRIBUTING.md)

The browser suite read against Playwright's own guidance — waiting, locators, isolation,
artifacts — after a day in which two failures showed up only in a full run and not when
their class ran alone. The question was not "are there bugs" but "does this suite tell us
what happened when it fails, and does it fail for the reason it says".

Reviewed at `17f1bf4`; changes landed in `#210`.

## What the grep said, and what was true

A search for fixed waits found twenty of them, which looked like the finding. It was not:

- Every `WaitForTimeoutAsync` in `AuthentikProvider`, `KeycloakProvider` and
  `AutheliaProvider` is inside `SignInForTheRecordingAsync` — the path that exists to make
  a demo video, where a pause between keystrokes is the point rather than a hope. The test
  path, `SignInAsync`, has none.
- Every `Task.Delay` is inside a polling loop with a deadline and a diagnostic failure,
  waiting on a container to come up or on an API to agree. That is the right shape for
  something a browser cannot observe.

One was real: a flat two seconds after clicking the login button, followed by reading
`page.Url`. Two seconds is a guess about a redirect chain that crosses to the identity
provider and back — long on a laptop, short on a loaded runner, where the test then
reported "still on the login page" about a login that was merely slower than the guess. It
is a `WaitForURLAsync` now, with the same diagnostic on timeout.

So the suite's waiting was in better shape than the first look suggested, and saying so is
part of the audit.

## What was missing: traces

The suite saved a screenshot and the page HTML on failure (`BrowserSupport`), and nothing
else. A screenshot shows the end state; it does not show which request failed, what the
console said three steps earlier, or what the DOM looked like when a locator did not match.

Both of today's full-run-only failures cost an extra round trip through CI — add a
diagnostic, push, wait, read — to find out what the browser had actually done. One was an
`EvaluateAsync` landing inside a navigation; the other was a provider refusing a redirect
URI whose port was missing. A trace answers both without the round trip.

`TracedContext` now records one per test that uses it: every action with the DOM before and
after, the console, the network. The name comes from the calling test method, so a site
that opens a context needs no name of its own. Traces are written always rather than on
failure, because xUnit does not tell a test whether it passed — five and a half megabytes
for eleven browser tests is a cheaper price than a second CI run — and they land in
`tests/runs/playwright`, which is gitignored and which CI uploads with the other artifacts
when a job fails.

Adopted in every browser class that drives the plugin. `ConfigPageTests` and
`LoginButtonLayoutTests` keep a context for the length of a test and write their traces at
teardown; `RealIdentityProviderTests` — the slowest tier, three real providers, and the one
where a CI-only failure costs the most to chase — owns its contexts per helper.
`ScreenshotTests` and `DemoRecordingTests` are left out on purpose: they exist to produce
a picture and a video, a person runs them on demand and watches them do it, and a trace of
a recording is weight without a question it answers.

## What was missing: the fixture put back as it was

The fake identity provider is one object shared by every test in the collection, and it has
a knob for each way a provider can misbehave: a userinfo endpoint that fails, one that
answers about somebody else, a document that moves its end-session endpoint, a missing
favicon, a rotated signing key. `ResetAsync` put the registered redirect URIs back and
nothing else, so each knob was unset only by the test that set it, in its own `finally`.

A test that fails before its cleanup therefore leaves the provider misbehaving for
everything that runs afterwards — and the failures that causes look like defects in the
plugin, two files away from their cause. That is not hypothetical: a rotated signing key
did exactly that this afternoon, and six tests in another class failed for it.

`ResetBehaviour` now puts every knob back, next to the knobs themselves, and
`ResetAsync` calls it. A knob added without a line there is a knob that leaks, which is at
least a visible rule.

Still not restored: the signing key itself, because that would mean keeping the original
around. Rotation is now harmless — each one gets its own key id, so the plugin refetches —
but it is a state a test leaves behind.

## Noted and not changed

**Web-first assertions.** The suite waits with `WaitForAsync` or `WaitForFunctionAsync` and
then reads a value to assert on. Playwright's `Expect` would retry the assertion itself,
which is both shorter and the thing that avoids the class of flake that bit
`PrefixedDeploymentTests` — reading a value while the page navigates under it. Converting
the whole suite is not worth the churn; writing new assertions that way is. The pattern to
copy is in `PrefixedDeploymentTests`: wait for the condition that means the page has
settled, and read afterwards.

**Console errors.** `BrowserLoginTests` collects them and asserts on a specific one;
`ConfigPageTests` collects them for its failure message; the rest ignore them. Failing on
any unexpected console error is a real option and would have caught at least one of the
sub-path defects earlier. It needs an allowlist for the noise Jellyfin's own client makes,
which is why it is a note rather than a change.

## The first failure after this landed, and what the trace was worth

The CI run of this very change failed — `e2e (12.1)`, one of three matrix jobs, while the
other two passed:

```
Microsoft.Playwright.PlaywrightException : net::ERR_NETWORK_CHANGED
  at https://host.docker.internal:28732/jellyfin/sso/oidc/start
```

Chromium abandons a navigation in flight when the host's network interfaces change under
it, and starting a container does exactly that: a bridge network appears and firewall rules
are rewritten. `PrefixedDeploymentTests` starts a proxy container moments before it
navigates, so it is the one class in the suite that races with Docker rather than with the
server. Two hundred and thirty-seven milliseconds in, and nothing to do with the plugin.

`BrowserSupport.GotoAsync` now retries that navigation, for the named errors only and with
anything else raised at once — a retry of one operation that failed for a stated reason,
which is a different thing from waiting a while and hoping.

The artifact from that failed job is the other half of the point: it carried eight traces,
including the failing test's own. The mechanism this change adds proved itself on the first
failure it saw, which is more than the change claimed for it.

## A mistake worth keeping

Adopting `TracedContext` broke one test, and the break is instructive. The context creation
in a helper became `await using var traced = ...` inside that helper, so the context was
disposed when the helper returned and the page it handed back belonged to nothing. The test
failed in 170 milliseconds with a Playwright error about a closed context.

A helper that opens a browser context must hand ownership to the caller, or the caller gets
a page that is already gone. It now returns the context alongside the page, and the test
owns both.

## Where the rest of the day's test findings are

This audit is about the browser suite. The catalogue question — would better tests have
caught the eight findings an outside review filed — is answered in
[that review](2026-11-05-outside-review.md), along with what came of it: four pairs of
capabilities the suite had and never crossed, three kinds of test it did not have at all
(a forced overlap, a failing writer, a stalled socket), and one measurement.
