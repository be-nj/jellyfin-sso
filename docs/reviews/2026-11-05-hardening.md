# Hardening review, 5 November 2026

[Documentation index](../README.md) · [Threat model](../threat-model.md) ·
[October's review](2026-10-19-security-and-quality.md)

One pass over the whole plugin with a single question: where does it accept something it
has not checked, and where does it claim a check it does not make? Not a search for bugs —
the suites look for those — but for the distance between what the code says about itself
and what it does.

Reviewed at `9c91a0d`; fixes landed in `#196`.

Twelve findings. None of them is a hole an outsider can walk through today: ten need the
identity provider, its discovery document or an administrator to misbehave, and the other
two need a provider whose users may choose their own display name. That is the honest
shape of this list. It is also the shape of the trust this plugin is built on — every one
of them is a place where "the provider is trusted" was doing work that a check should be
doing instead.

## What came out of it

| # | Finding | Severity | Issue |
|---|---|---|---|
| 1 | A token minted for another application was accepted when its audience also named this one | medium | [#184](https://github.com/be-nj/jellyfin-sso/issues/184) |
| 2 | A symmetric key in the provider's JWKS would have decided whether a token is genuine | medium | [#185](https://github.com/be-nj/jellyfin-sso/issues/185) |
| 3 | The userinfo answer was never checked to be about the person who logged in | medium | [#186](https://github.com/be-nj/jellyfin-sso/issues/186) |
| 4 | The host check for the end-session endpoint was written, tested, and never called | medium | [#187](https://github.com/be-nj/jellyfin-sso/issues/187) |
| 5 | An administrator's unlink left tokens nobody could end | medium | [#188](https://github.com/be-nj/jellyfin-sso/issues/188) |
| 6 | Names and groups from a token went into the log unescaped | low-medium | [#189](https://github.com/be-nj/jellyfin-sso/issues/189) |
| 7 | A padded username created a second account that reads like the first | low-medium | [#190](https://github.com/be-nj/jellyfin-sso/issues/190) |
| 8 | The back channel followed redirects | low | [#191](https://github.com/be-nj/jellyfin-sso/issues/191) |
| 9 | A secret file that cannot be read answered 500 and named the path | low | [#192](https://github.com/be-nj/jellyfin-sso/issues/192) |
| 10 | A library id that is not one turned every login into a 500 | low | [#193](https://github.com/be-nj/jellyfin-sso/issues/193) |
| 11 | The logout endpoint was the one anonymous endpoint without a limit | low | [#194](https://github.com/be-nj/jellyfin-sso/issues/194) |
| 12 | An implausible `iat` in a logout token answered 500 | low | [#195](https://github.com/be-nj/jellyfin-sso/issues/195) |

## Every finding, and what now holds it

### 1. Several audiences, no authorized party

`aud` was checked by asking whether this client is **one of** the names in it, which is
what the library does by default. A provider can be configured to put a second client's id
in a token's audience — Keycloak has an audience mapper for exactly that — and such a
token was issued for the other client. OIDC Core §3.1.3.7 covers it: where there are
several audiences, `azp` names the one the token was issued for, and it has to be this one.

Held by `A_token_naming_several_audiences_without_azp_is_rejected`,
`A_token_naming_several_audiences_for_another_party_is_rejected` and
`A_token_naming_several_audiences_for_this_party_is_accepted`.

### 2. The algorithm was whatever the keys allowed

`ValidAlgorithms` was not set, so the acceptable algorithms were whatever the library would
take against the keys it had been handed. A JWKS is a public document. An `oct` entry in
one — which no provider has a reason to publish — would have made its own public bytes the
HMAC secret that decides whether a token is genuine, on both the login and the
back-channel-logout path. The algorithms are now named, and they are the asymmetric ones.

Held by `A_token_signed_with_a_symmetric_key_from_the_key_set_is_rejected` and
`A_logout_token_signed_with_a_symmetric_key_is_rejected`, both of which hand the validator
the very key the token was signed with.

### 3. The userinfo answer was taken on trust

Where groups come from the userinfo endpoint, or where the ID token carried no name, the
answer from that endpoint decides a user's groups — which is to say their libraries and
their administrator flag. OIDC Core §5.3.2 requires its `sub` to be verified against the
ID token's, and requires the response not to be used when they differ. It was not checked.
It is now: a mismatch fails the login where the answer was needed, and is passed over
where only a profile picture was.

Held by `A_userinfo_answer_about_somebody_else_is_not_used`.

### 4. A check that was tested but not called

The discovery document names the addresses the plugin hands something to: the signing
keys, the token endpoint, the userinfo endpoint — and the end-session endpoint, which
receives the user's own ID token as an `id_token_hint`. The host check was written for all
four. Its comment said four. Its test, `DiscoveryHostTests`, passed four and asserted that
each one is refused on another host. The call passed three.

So the suite asserted a protection the server did not have, which is the one shape of
defect that a passing test actively hides. The call now passes the end-session endpoint
too, and the test that holds it drives a login rather than the rule:
`A_document_that_moves_the_end_session_endpoint_is_refused`.

The authorize endpoint is deliberately still not checked, and the reason is written at the
call: nothing is handed to it, and where a provider puts the form people type into is its
own arrangement — a login UI on its own hostname is a layout several products offer.

### 5. The administrator's unlink did not end the sessions

The self-service unlink ends the account's sessions, and
[#131](https://github.com/be-nj/jellyfin-sso/issues/131) says why: back-channel logout
finds an account through its link and nothing else, so once the link is gone nothing can
end those tokens and Jellyfin does not expire them. The Links tab's own unlink was never
given the same treatment — and an administrator unlinking somebody is usually cutting off
their access, which is the reading under which it matters most.

Held by `An_administrators_unlink_ends_the_sessions_too`.

### 6. A name from a token could forge a log line

The provider id and the OAuth error were already mapped to closed sets before reaching the
log, for exactly this reason. A username, a group name and a subject are not from closed
sets: at a provider that lets people choose their own display name they are whatever that
person typed, and a name carrying a newline and a plausible line after it reads as a second
entry the server wrote. They still reach the log, because an operator working out why
somebody landed in the wrong libraries needs the name the provider sent — but on one line,
with control characters replaced and the length bounded.

Held by `A_name_from_a_token_reaches_the_log_on_one_line`,
`A_name_long_enough_to_fill_a_log_is_cut` and
`A_group_name_from_the_provider_cannot_forge_a_log_line`.

### 7. The helper called `Trimmed` did not trim

It returned null for a blank value and otherwise the value unchanged. The subject must not
be trimmed — it is an opaque identifier matched byte for byte against a stored link, and
changing it would break every account whose provider pads it. The username must be: a
provider sending `" admin"` found no collision with the existing `admin`, because
Jellyfin's own lookup does not pad, and the plugin then asked Jellyfin to create a second
account whose name reads the same in every list it appears in. The two now have separate
helpers, each named for what it does.

### 8. The back channel followed redirects

Every address the OIDC client calls was either configured by an administrator or published
by the provider and checked against the authority's host — and a redirect is a way to move
the call after that check. The authorization code and the code verifier are in the body of
the token request, and a 307 to another host resends the body there. Redirects are no
longer followed; the three providers in the test matrix answer their published endpoints
directly, as the real-provider tier confirms.

### 9. A secret file that cannot be read

`File.ReadAllText` on an unreadable path throws an `IOException`, which is not one of the
types the anonymous callback catches — so it answered 500 with the framework's message in
the body, and that message names a path inside the container. It is now an
`InvalidOperationException` that names neither the path nor the reason, with the reason
kept as the inner exception for the log.

Held by `A_file_that_is_not_there_is_reported_without_naming_it`.

### 10. A library id that is not one

A library is named by its Jellyfin id and those ids are parsed on every login, so one that
is not a GUID threw a `FormatException` out of the permission sync — another type the
callback does not catch. Every SSO login on the server answered 500, from a value only the
admin endpoint can introduce. The endpoint refuses it now, and the sync skips one that got
in another way and says which it was.

Held by `A_library_id_that_is_not_one_is_refused_at_the_door` and
`A_library_id_that_is_not_one_grants_nothing_and_is_named`.

### 11. The logout endpoint had no limit

Every other anonymous endpoint that costs the server work is behind the request limiter. A
logout fetches the provider's discovery document when the cache has none, and a failed
fetch is not cached, so an anonymous caller in a loop against a provider that is down had
the server open an outbound request per call and wait twenty seconds for each. Sixty a
minute is far more than a person pressing sign out.

### 12. An implausible `iat`

`DateTimeOffset.FromUnixTimeSeconds` throws for a number outside the years it can hold, so
a signed logout token carrying `"iat": 9223372036854775807` answered 500 instead of being
refused as a token. Range-checked before it is converted.

Held by `A_token_whose_issued_at_is_not_a_plausible_time_is_refused`.

## Looked at and left alone

Four things that looked like findings and are not, written down so the next pass does not
spend time on them again:

- **The ephemeral login secret and the Quick Connect approval have no cap**, where pending
  logins do. The difference is who can fill them: anybody can start a login, while both of
  these are only issued after one has succeeded at the provider. A 90-second and a
  two-minute life, with a purge on every write, is the whole of what they need.
- **`EphemeralSecretStore.Consume` and the browser-id comparison are not constant-time.**
  Both values are 256 bits of randomness looked up in a hash table, so there is no guessing
  game for a timing signal to help with. The comment on the browser id already says so.
- **A provider may be configured over plain HTTP.** Refusing one would be a real
  protection and would also break every deployment where Jellyfin and the provider share a
  private network, which is the common homelab shape. It stays the operator's decision, and
  the threat model says it is outside what the plugin can repair.
- **The request limit counts addresses, not people, and the forwarded headers are taken
  from whoever sent the request.** Both are known gaps with the reasoning written down —
  the alternative to the first is a limit keyed on a header the caller writes, and the
  second is closed properly by a proxy that strips client-supplied forwarded headers. The
  shapes those values are allowed to have is the defence, and it was checked again here.

## What this pass says about the suite

Finding 4 is the one worth keeping in mind. Eleven of these twelve are ordinary
hardening — a check that was not there is now there. That one was different: the check
existed, the test existed, the test passed, and the protection was absent, because the test
exercised the rule and the call site passed a shorter list. No count of tests and no
mutation score would have caught it; the mutate list does not include the call site, and a
mutation of the argument list is not a mutation Stryker makes.

What catches that shape is asking of each protection not "is it tested" but "is the test
driving the thing that runs in production". The two e2e tests added here do that for the
discovery document and for the userinfo answer. The same question is worth asking of the
rest of `DiscoveryHostTests`' siblings: a rule tested directly is only evidence about the
rule.

## Method

One reader, one pass, reading for the difference between the claim and the code: every
comment that says a check happens, every row in the threat model, every helper whose name
promises something. Then the places where a value crosses from outside to inside — a claim
in a token, a field in a discovery document, a value in the configuration file, a header —
and for each of them, what would happen if it were hostile rather than merely wrong.

Not covered: the browser-facing pages (October's review and `SsoPagesTests` cover the
escaping, and nothing changed there), the Jellyfin APIs this plugin calls, and the
dependencies — those are watched by the weekly upstream run and by Scorecard.
