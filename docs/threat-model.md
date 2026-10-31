# Threat model

[Documentation index](README.md) · [README](../README.md) · [SECURITY.md](../SECURITY.md)

What this plugin trusts, what it does not, and which test holds each claim. Written so a
reader who trusts neither the author nor a model can check the claims one by one.

## What is trusted

- **The identity provider**, for who a person is and which groups they hold. The plugin
  verifies that the statement came from that provider and was meant for this server; it
  cannot judge whether the provider's answer is correct.
- **TLS to the provider.** Discovery and JWKS are fetched over the configured issuer URL.
  A provider reached over plain HTTP, or with a certificate the server does not trust, is
  outside what this plugin can repair.
- **Jellyfin's own enforcement** of the user policy this plugin writes.
- **The administrator**, who configures providers and mappings. An administrator can
  already do everything the plugin does.

## What is not trusted

The browser, every query parameter, the body of any request, the claims in a token before
its signature is checked, and the group names that arrive with it.

## Attacks and what stands in the way

| Attack | What stops it | Test |
|---|---|---|
| Forged ID token | Signature checked against the provider's JWKS | `A_token_signed_by_an_untrusted_key_is_rejected`, `An_unsigned_token_is_rejected` |
| Token minted for another application | `aud` must equal this provider's client id | `A_token_for_another_client_is_rejected` |
| Token from a different provider | `iss` must equal the issuer from discovery | `A_token_from_another_issuer_is_rejected` |
| Replayed token from an earlier login | `nonce` must equal the one this login issued | `A_replayed_token_from_another_login_is_rejected`, `A_token_without_a_nonce_is_rejected` |
| Expired token | Lifetime checked, 60 s skew | `An_expired_token_is_rejected` |
| Replayed callback (`state`) | `state` is single-use and carries its own provider and redirect URI | `A_replayed_callback_state_is_refused` |
| Stolen authorization code | PKCE S256; the verifier never leaves the server | `PendingStateStoreTests` |
| Stranger registers an existing username at the provider | Accounts are keyed by `(provider, sub)`; a colliding name is refused until an administrator links it | `An_unlinked_identity_cannot_take_over_an_existing_account` |
| Reusing the one-time login secret | Single-use, 90 s, bound to one username | `An_ephemeral_secret_works_once`, `An_ephemeral_secret_only_works_for_its_own_user` |
| Anonymous call to the admin API | Every data endpoint requires an administrator | `The_admin_api_is_closed_to_anonymous_callers` |
| Group rename at the provider silently keeping access | Permissions are rewritten from the groups on every login | `Access_is_revoked_when_the_groups_disappear` |
| Mistyped admin group locking everyone out | The last administrator is never demoted | `PermissionSyncTests` |
| A provider configured to let in more people than it should | Optional: *Refuse logins that match no mapping* turns away a login that matches no group mapping, before an account exists, and keeps doing so for accounts that already exist. Off by default, because the provider is the usual place for this decision; on, it is a second lock that does not depend on the provider staying right | `LoginGateTests`, `A_login_in_no_mapped_group_is_refused_once_the_server_asks_for_one`, `An_account_that_loses_its_group_loses_the_login_too` |
| Claims smuggled under another name | Only the configured groups claim is read | `Claims_under_a_different_name_are_not_read_as_groups` |
| Forged log lines through a crafted provider id | Only configured ids and the fixed RFC 6749 error codes are logged | `LoggableTests` |
| Provider name injecting markup into the login page | Names are HTML-escaped, ids URL-encoded | `A_provider_name_cannot_inject_markup` |
| Someone else's identity linked to your account | A link to an identity another account holds is refused | `SessionAndLinkingTests` |
| Forged back-channel logout ending a session | The logout token is checked against the provider's JWKS, issuer and audience | `A_token_signed_by_nobody_the_provider_knows_is_refused`, `A_token_signed_by_somebody_else_is_rejected` |
| An ID token replayed as a logout token | A logout token must carry the logout event and must not carry a `nonce` | `An_id_token_cannot_be_replayed_as_a_logout_token`, `A_token_without_the_logout_event_is_rejected` |
| A captured logout token replayed later | `iat` is required and may be at most five minutes old | `A_token_captured_earlier_cannot_be_replayed_later` |
| A stolen logout handle ending somebody's provider session | The cookie holds an opaque 32-byte handle, never the token; it is `HttpOnly`, single-use, scoped to `/sso/oidc`, and `Secure` whenever the server's external address is `https`. Worst case for an interceptor on a plain-HTTP deployment is ending a session that was about to end. Nothing is kept while *Sign out of the identity provider too* is off, and what is kept is in memory only, for at most 30 days or 10,000 browser logins | `LogoutSessionStoreTests`, `Nothing_is_retained_while_the_provider_logout_is_switched_off` |
| An ID token sent as a hint to the wrong provider | The retained login records the provider id, authority and client id, and the hint is sent only when all three still match | `A_token_is_not_used_for_a_different_provider_or_changed_configuration`, `Logout_does_not_send_a_retained_token_after_the_client_id_changes` |
| A logout for one account signing out another | The subject is matched by `(provider, sub)`, as a login is | `Another_users_logout_leaves_this_session_alone`, `SessionRevokerTests` |
| Flooding the anonymous login endpoints | 60 requests per minute per address, answered with 429 and a Retry-After; the count has an end of its own | `A_caller_past_the_limit_is_turned_away_and_told_when_to_come_back`, `LoginRateLimiterTests` |
| A picture claim, or a brand document, pointing the server at something else | One fetcher for both: absolute `http(s)` only, at most three redirects, a size counted while the body arrives rather than read off a header, and only the image types Jellyfin's own pipeline reads — SVG is a document that can carry script, and is refused. Nothing that comes back can fail a login. A provider that has been taken over can still make the server issue those requests inside its own network, which is why profile pictures are off by default and say so | `ProfilePictureSyncTests`, `A_picture_that_is_not_an_image_is_refused_and_the_login_still_works` |
| Filling the server's memory with abandoned logins | Pending states expire after ten minutes and are capped at 10 000 | `PendingStateStoreTests` |

## Known gaps

- **A group change waits for the next login.** Removing someone from a group at the
  provider changes their permissions only when they sign in again. A session *ended* at
  the provider now ends in Jellyfin too, where the provider sends back-channel logout
  (Authentik, Keycloak; not Authelia) — see
  [Back-channel logout](login-and-logout.md#back-channel-logout). Changing a group without
  ending the session still needs Jellyfin's own user screen, or ending the session under
  Dashboard → Devices. See [Security notes](security.md).
- **Quick Connect needs the person to approve.** Opening `/sso/oidc/start/qc?code=…` and
  signing in no longer authorizes the code on its own — the login ends on a page that
  names the account and shows the code, and approval is a separate click
  ([#1](https://github.com/be-nj/jellyfin-sso/issues/1)). A household member who is sent
  such a link therefore sees whose code they are about to let in.
- **The limit counts addresses, not people.** Everyone behind one router is one caller,
  and a caller with an IPv6 prefix to walk is many. Sixty requests a minute is roomy
  enough that the first case does not bite and tight enough that the second gains little;
  it is a ration on work, not an authentication decision. See
  [Configuration](configuration.md).
- **The plugin cannot restrict who a provider admits.** That gate belongs at the provider
  (Authentik policies, Keycloak client scopes, Authelia access rules). Providers without
  per-application assignment — Google among them — therefore admit anyone with an
  account, which is why they are not documented as supported.

## How the claims are kept honest

- 354 unit tests and 85 browser-driven end-to-end tests run on every push, against
  Jellyfin 12.0, 12.1 and 12.2.
- Every release signs in through real Authentik, Keycloak and Authelia containers.
- CodeQL runs on every push, and Scorecard rates the repository from outside. Alerts that
  are dismissed carry the reason at the alert.
- Mutation testing breaks the code on purpose — a `<` into a `<=`, a condition into its
  opposite — and counts how many of those changes the tests notice: 70 %, with the build
  failing below 65 %. It answers what a passing suite cannot, which is whether the tests
  would catch a defect at all.
- Release artifacts carry a provenance attestation (`gh attestation verify`) and are
  byte-reproducible from their tag — the recipe is in
  [CONTRIBUTING.md](../CONTRIBUTING.md).
- Mutation testing checks that those tests would notice a defect at all; the score and its
  floor are in CI.
- Every defect found in this plugin is filed as an issue with the test that now covers it,
  including the ones its own author found — see the
  [found-in-review](https://github.com/be-nj/jellyfin-sso/issues?q=label%3Afound-in-review)
  label.
