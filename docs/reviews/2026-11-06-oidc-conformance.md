# OpenID Connect conformance run, 6 November 2026

[Documentation index](../README.md) · [Threat model](../threat-model.md) ·
[Security notes](../security.md) ·
[The outside review the day before](2026-11-05-outside-review.md) ·
[The harness](../../tests/conformance/README.md)

The first time this plugin was checked against the specification by something that
implements the other side of it. The OpenID Foundation's conformance suite was run
against it as a relying party: thirty-six modules across two plans, the suite acting as
the identity provider, the plugin doing a real login or a real logout against each one.

Reviewed at `19c3726`; fixes landed in `#221`.

Why this and not a fourth model: a review reads the code and reasons about what the
specification probably says. The suite sends the broken responses and records what the
plugin did with them. Those are different kinds of evidence, and the day before had
already shown what the first kind had left — a review from outside found eight real
findings in a plugin that had been through five internal passes.

## What came out of it

| # | Finding | Severity | Issue |
|---|---|---|---|
| 1 | An ID token with no `iat` claim was accepted | moderate | [#216](https://github.com/be-nj/jellyfin-sso/issues/216) |
| 2 | A logout token signed with an unexpected algorithm was accepted | low | [#219](https://github.com/be-nj/jellyfin-sso/issues/219), closed the next day with a per-provider setting |
| 3 | Keys published for encryption were accepted as signing keys | low | [#220](https://github.com/be-nj/jellyfin-sso/issues/220) |
| 4 | A refusal message named the wrong outcome | low | [#217](https://github.com/be-nj/jellyfin-sso/issues/217) |
| 5 | `E2E_STACK_MINUTES` never reached the container | — | [#218](https://github.com/be-nj/jellyfin-sso/issues/218) |

Both plans were run again the next day against the fixed plugin, and the two reruns are
the last two sections of this page. Every fix above is confirmed by the suite rather than
only by the tests written to close it, which matters most for the first finding: the test
that covers it was written by the same reader of the specification who had not noticed the
claim was unchecked.

## The one that was a real gap

**`oidcc-client-test-missing-iat`.** What the suite requires, in its own words:

> The client must identify the missing 'iat' value and reject the ID Token after doing ID
> Token validation.

What it saw:

```
Client has incorrectly called userinfo_endpoint after receiving an id_token
with no iat claim.
```

OpenID Connect Core lists `iat` among an ID token's required claims (§2) and tells the
client to validate it (§3.1.3.7, step 9). Microsoft's `JwtSecurityTokenHandler` has
`RequireExpirationTime`, `RequireAudience` and `RequireSignedTokens`, and nothing for this
one, so nothing required it.

Two things hid it, and the second is the more interesting:

- `LogoutTokenValidator` **does** check `iat`, with a range check and a maximum age, and
  [security.md](../security.md) said so. Reading the documents, the claim looked covered.
  It was covered on the logout token and never on the ID token.
- `IdTokenFactory.Mint`, which mints the tokens most of the unit suite runs on, **did not
  set `iat` at all**. The `JwtSecurityToken` constructor it uses writes `nbf` and `exp` and
  has no parameter for it. So the suite's own provider emitted tokens no conforming
  provider would issue, and a test asserting the claim was required would have failed
  against the factory rather than against the plugin. Adding `iat` to the factory broke
  twelve existing tests before a single new one was written.

Not a session-stealing hole, and worth saying so: `exp` is required and validated, and the
`nonce` ties a token to the browser that began the login, so a token with no `iat` is still
short-lived and still single-use. What was missing is a required claim and the only one by
which a token's age can be bounded.

`RequireIssuedAt` now checks presence, shape, a plausible range, and a token claiming to
come from the future. Deliberately no maximum age — that is what `exp` is for, and
inventing one here would refuse providers that legitimately issue long-lived ID tokens.
The range check comes before the conversion for the same reason as on the logout token:
`DateTimeOffset.FromUnixTimeSeconds` throws `ArgumentOutOfRangeException` outside the
representable years, and a signed token carrying `"iat": 9223372036854775807` would answer
500 with the library's message in the body instead of refusing the token.

## The one that needed a setting

**`oidcc-client-test-rp-backchannel-rpinitlogout-wrong-alg`.** The suite set
`id_token_signed_response_alg` for its registered client to the algorithm of the first key
in its set — an RSA key — and then signed the logout token with `ES256` using a different
key from the same published set. The plugin accepted it and ended the account's sessions.

`ValidAlgorithms` is the whole asymmetric set, so an `ES256` token signed by a published EC
key is inside it. The RS256-to-HS256 substitution that pinning was added for the day before
is still refused, and so is `alg=none`; what is not refused is substitution among the
provider's own asymmetric keys.

It is not a forgery path — every key involved is one the provider published and holds the
private half of. It is a conformance failure, and it is the client declining to pin what it
was told to expect.

Two tightenings that look like the fix are not:

- Intersecting with `id_token_signing_alg_values_supported` does nothing here, because this
  provider's discovery document has no such field. And it has a real failure mode in the
  other direction: a provider whose advertised list omits what it actually signs with would
  stop working, and the symptom is that nobody can log in.
- Filtering the key set is right for its own reasons (below) but this module's key is a
  signing key.

What would close it is the thing the specification assumes and this plugin does not have: a
notion of the algorithm a given provider signs with. The plugin is a static client and
registers nothing. That is [#219](https://github.com/be-nj/jellyfin-sso/issues/219), filed
with the analysis and not implemented in the same pass — it is a configuration field, an
admin validation and a page control, which is a feature rather than the fix to a defect.

**Built the next day**, as *Signing algorithms* on the provider card: empty by default and
therefore no change to any existing deployment, and filled in it refuses a token signed
with anything else, on the login path and the logout path alike. Two things about it were
decided by how the library behaves rather than by taste. The setting is intersected with
the asymmetric set, so a configuration naming `HS256` cannot undo the pinning it exists to
tighten. And the result is never an empty list, because
`An_empty_algorithm_list_would_let_the_library_accept_anything` demonstrates what the name
says: with no entries in `ValidAlgorithms` the handler skips the algorithm check, so a
field full of typos would have accepted more than the default did. A list of names the
plugin does not recognise therefore falls back to the default set, and `SaveConfig` refuses
one at the door so that fallback stays a safety net rather than a behaviour anybody relies
on. Seven of the eighteen new tests fail if the pinning is removed again.

## The one that came out of reading the suite's key set

The same provider publishes thirteen keys, and two of them are not signing keys:

```
kty=RSA  alg=RSA-OAEP  use=enc
kty=EC   alg=ECDH-ES   use=enc  crv=P-256
```

`SigningKeysFrom` handed every key in the set to the validator, those included. RFC 7517
§4.2 is exactly what `use` is for. Again not a forgery path — the provider holds those
private keys too — but an encryption key is the one most likely to live somewhere else: a
different slot, a different rotation, sometimes shared with whoever is meant to encrypt to
it. That is a different trust boundary from the key that says a token is genuine.

The helper had no test of its own, which is the other half of why nobody noticed. It has
four now. A key with no `use` is still accepted, because the parameter is optional and
providers that publish one key routinely omit it.

The suite also publishes five deliberately unusable keys — a post-quantum-shaped key with a
non-existent parameter set, a made-up key type, an RSA key and an EC key with unknown
algorithms, and an EC key with an unknown curve — and expects a conformant client to ignore
them and verify with the rest. The plugin did, before and after this change.

## What held

Ten refusals on the login path, each one the plugin's own decision with its own reason in
the log, read out of the server rather than taken from the suite's verdict:

```
IDX10504  no signature                     oidcc-client-test-idtoken-sig-none
IDX10511  signature does not check out     oidcc-client-test-invalid-sig-rs256
IDX10511  signature does not check out     oidcc-client-test-invalid-sig-es256
IDX10214  audience validation failed       oidcc-client-test-invalid-aud
IDX10206  audiences empty                  oidcc-client-test-missing-aud
IDX10205  issuer validation failed         oidcc-client-test-invalid-iss
          ID token nonce mismatch          oidcc-client-test-nonce-invalid
          'sub' is missing or blank        oidcc-client-test-missing-sub
          userinfo answered about another   oidcc-client-test-userinfo-invalid-sub
          subject
```

and six of the eight back-channel logout modules — all eight once the algorithm was
pinned, see the rerun below: a valid token ends the sessions, and a
token that is unsigned, carries a `nonce`, carries no `events` claim, names another event,
or names another audience is refused.

Two things proved themselves that had only ever been tested against this repository's own
fake provider:

- **Key rotation.** `A token from Conformance suite was signed by an unknown key;
  refetching its keys` appears in the log, and the login then completed. Both
  `oidcc-client-test-signing-key-rotation` and the harder
  `-just-before-signing` variant passed.
- **The userinfo subject binding** from
  [#190](https://github.com/be-nj/jellyfin-sso/issues/190), a day old, refusing a provider
  that deliberately answered about somebody else.

## Deliberately not supported

Two modules fail because the plugin does not do something, and should not start:

- **`userinfo-bearer-body`** wants the access token as a form parameter in the request
  body. The plugin sends the `Authorization` header. RFC 6750 §2.2 calls the body form
  optional and says it "SHOULD NOT be used except in application contexts"; a client that
  only uses the header is correct.
- **`userinfo-signed`** returns a signed JWT from the userinfo endpoint. A client gets that
  only if it registered `userinfo_signed_response_alg`, which this plugin never does. It
  refuses rather than reading an unverified payload, which is the right direction to fail
  in.

Three more do not apply at all: two WebFinger discovery modules, and
`client-secret-basic`, which needs a confidential client where this run used the plugin's
default public one.

## What the harness got wrong, and why that matters here

Three verdicts in this run were the harness's fault, not the plugin's, and all three
initially looked like findings.

**The classifier asked the wrong question.** It recorded whether the login completed, and
called "did not complete" a pass for every module that required a refusal. That is not the
same claim: `discovery-issuer-mismatch` failed its first run with the flow stopped at the
suite's authorization endpoint, which means the *suite* refused it, not the plugin. Marked
`OK` by a check that could not tell the difference. Re-attributing by the host of the final
address is what turned eleven green ticks into ten green ticks and one question.

**The discovery cache.** That question had an answer: the plugin caches a provider's
discovery document for thirty minutes under `{provider id}|{authority}`, and every module
in a plan shares one issuer, so the plugin went straight to the authorization endpoint with
endpoints it already had. The suite said as much —
`Got unexpected HTTP call to authorize before the discovery endpoint call` — and the first
attempt at a fix was wrong too: restarting the container empties the cache, but
Testcontainers publishes the server on an ephemeral port and Docker hands out a new one on
restart, which invalidated the redirect URI the suite had registered and killed the stack
mid-run. Moving the *provider id* moves the cache key without touching anything else. With
a cold cache the plugin refuses an issuer mismatch at the start endpoint, before the browser
goes anywhere, and the module passes.

**All eight logout modules failed identically** on their first run, including the one that
has to succeed, with `RestClientException happened whilst calling logout endpoint`. Eight
identical failures including the positive case is the shape of a harness fault, which is the
same lesson as the six identical one-millisecond failures the day before. The
`backchannel_logout_uri` was given as `http://localhost:…`, and inside the suite's own
container `localhost` is that container. `host.docker.internal` is the Docker host from
there.

Worth writing down because the first reading of each of the three was a finding, and all
three would have been false. The rule that keeps earning its place: a verdict is only
evidence about the plugin once you know who produced it.

## Still inconclusive, and said so

`discovery-openid-config` and `discovery-jwks-uri-keys` finish as soon as the relying party
has fetched the document and the keys — which is all they test. The harness then drives a
full login, and the request that follows arrives after the test has closed, so the suite
records `Illegal test state change: FINISHED -> RUNNING` and a failure. The plugin did the
required thing; the recorded verdict is FAILED, and that is not being written up as a pass.
`rpinitlogout-wrong-iss` looked like the same story on the logout side — the plugin refused
the token with `IDX10205` in its own log, and the verdict read FAILED. It passed on the
rerun a day later, unchanged, so that one was timing in the harness rather than anything
about the module: the late request is a race, not a certainty.

The second rerun, below, got as far as this can get without the plugin growing a way to
fetch a discovery document on demand: the harness now records whether a module reached
`Test has run to completion` and which failures, if any, landed *before* it did. For both
of these the answer is yes and none, which is a positive statement about the run and still
not a pass — the suite's verdict is what the suite publishes, and it says FAILED.

Driving only a discovery fetch would settle all three, and there is no way to ask the plugin
for one: it fetches the document when a login starts and at no other time. There is no
"test this provider" action anywhere in the plugin, so an operator who has just configured
one learns whether it works by trying to log in. That is a gap of its own and not a security
one.

## The first rerun, 7 November: the logout plan

The logout plan again, against the same server with *Signing algorithms* set to `RS256`,
which is what the suite registers for its client. **Eight of eight.**

```
PASSED  rpinitlogout              a valid token ends the sessions
PASSED  rpinitlogout-alg-none
PASSED  rpinitlogout-no-event
PASSED  rpinitlogout-with-nonce
PASSED  rpinitlogout-wrong-alg    the one this run found
PASSED  rpinitlogout-wrong-aud
PASSED  rpinitlogout-wrong-event
PASSED  rpinitlogout-wrong-iss    FAILED above, on timing
```

Attributed the way every other verdict here is — by who stopped the flow, out of the
server's own log rather than the suite's word for it:

```
09:15:16 [INF] Back-channel logout from provider conformance ended the sessions of 1 account(s).
09:21:27 [WRN] Rejected a back-channel logout for provider conformance: IDX10511:
               Signature validation failed. Keys tried: ... Use: 'sig', Kty: 'EC'
```

The second line is `wrong-alg`, and it names the EC key the suite signed with. Worth
reading closely: the refusal arrives as a signature failure rather than as "that algorithm
is not allowed", because `ValidAlgorithms` takes the algorithm out of consideration before
the key is tried, and then no key validates. The outcome is the right one and the message
is a layer lower than the decision — which is worth knowing before somebody reads that
line in a support thread and goes looking for a key problem.

The first line is the positive case, from the same configuration. Pinning the algorithm
does not cost the ordinary logout, which is the half of the acceptance that a passing
refusal does not prove.

## The second rerun, 7 November: the login plan

The logout plan proved its own fix. The login plan had not been run again since, which left
two of the findings — the missing `iat` and the encryption keys — verified only by the unit
tests written to close them. Those tests were written by the same reader of the
specification who had missed the gap in the first place, which is the whole reason this tier
exists. So the plan was run again, twenty-eight modules, against a server built from `6193f63`.

**Twenty-three pass, three do not apply, and two are the discovery cache.** No finding, and
nothing changed about the plugin between the two runs.

The one the rerun was for, in the plugin's own words rather than the suite's:

```
19:13:51 [WRN] Rejected a token from provider conformance:
               The ID token carries no 'iat' claim, which OpenID Connect requires.
```

Yesterday that same module recorded `Client has incorrectly called userinfo_endpoint after
receiving an id_token with no iat claim`. The refusal is the plugin's, it names the claim,
and it is the sentence an operator reads in the log.

Everything that held before held again: ten refusals each with their own reason in the
server's log, both key-rotation modules, and the userinfo subject binding. A token with no
`kid` validated against a single-key and a multiple-key JWKS, as it did before.

That last part is where the encryption-key fix got its live check. The suite publishes
thirteen keys at this issuer and still does: six usable signing keys, five deliberately
unusable ones, and the two with `use: enc` that finding three is about.

```
kty=RSA  alg=RSA-OAEP      use=enc
kty=EC   alg=ECDH-ES       use=enc   crv=P-256
kty=AKP  alg=ML-DSA-9999   use=sig
kty=RSA  alg=RS9999        use=sig
kty=EC   alg=ES9999        use=sig   crv=P-256
kty=EC                     use=sig   crv=P-9999
kty=OIDF-CONFORMANCE-UNSUPPORTED
```

So every login that completed in this run, and every refusal that named a signature,
verified against a set the plugin had to filter first — and the one that refused a
rotated key refetched and then found the right one among them.

Two modules refuse by design and are written up above — `userinfo-bearer-body` and
`userinfo-signed`. The harness used to report them as questions; it now has a name for
"the plugin refused this on purpose", checked exactly like any other required refusal.

### Three harness faults, found by rerunning rather than by reading

- **The classifier still handed out a false pass.** `discovery-issuer-mismatch` came back
  `OK` on the warm run, from a check that asked whether the login completed. It had not
  completed — because the *suite* stopped it, at its own authorization endpoint, which for a
  module requiring the client to refuse is the opposite of a pass. The rule was written down
  after the first run and applied in `run_discovery.py` only; `run_all.py` kept the weaker
  check. Both scripts now share one `who_stopped`, and that module now reads `LOOK` with the
  suite named, which is the honest answer on a warm cache. Cold, with its own provider id, it
  passes: `suite=PASSED  stopped_by=the plugin`.
- **The driver pinned an algorithm the login plan should not have pinned.** `configure_provider`
  had been left defaulting to `RS256` after the logout rerun, which is what that one plan
  needs. On the login plan it would have made `invalid-sig-es256` refuse the token for its
  algorithm before the signature was ever checked — the module would have passed without
  testing what it is for. The default is the plugin's default shape again, and the one plan
  that needs the pin sets it itself.
- **A FAILED verdict was being read by hand.** The two discovery modules end before the login
  the harness drives, so their verdict is about the harness. The harness now records whether
  each module reached `Test has run to completion` and which failures preceded it — for both,
  `ran_to_completion=True` with nothing failing before. Also visible there, and worth having
  on the record: `jwks-uri-keys` publishes its key set at a random path
  (`/jwks` + ten random characters), and the suite's log shows the plugin fetching exactly
  that one, so it read `jwks_uri` out of the document rather than assuming where it would be.

All three were mistakes in the thing doing the judging, which is the same lesson as the
first run's three and the reason the rerun was worth the twenty minutes: a harness that is
only ever run once is a harness whose verdicts nobody has checked.

## How to run it again

[tests/conformance/README.md](../../tests/conformance/README.md). It needs the manual
end-to-end stack for a server with the plugin in it, the suite's prebuilt containers, and
about twenty-five minutes. Not wired into CI: it wants two container stacks and a quarter
of an hour, which is the same reason the real-provider tier is run by hand.

One thing in the plugin's own source exists for it. The suite's nginx image ships a
certificate for `CN=localhost` with no subject alternative name at all, which no current
TLS stack accepts for any hostname — and the plugin's discovery client refuses plain HTTP
off loopback, so there is no sidestepping it. `TestCertificate` now carries the suite's
hostname as well, and the suite is given the certificate the stack already mounts into the
Jellyfin container as a trusted CA.
