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
| Claims smuggled under another name | Only the configured groups claim is read | `Claims_under_a_different_name_are_not_read_as_groups` |
| Forged log lines through a crafted provider id | Only configured ids and the fixed RFC 6749 error codes are logged | `LoggableTests` |
| Provider name injecting markup into the login page | Names are HTML-escaped, ids URL-encoded | `A_provider_name_cannot_inject_markup` |
| Someone else's identity linked to your account | A link to an identity another account holds is refused | `SessionAndLinkingTests` |

## Known gaps

- **Revocation is not immediate.** Removing someone from a group at the provider changes
  nothing until their next login. The remedy today is Jellyfin's own user screen plus
  ending the session under Dashboard → Devices. See [Security notes](security.md).
- **Quick Connect approval has no second step.** Opening
  `/sso/oidc/start/qc?code=…` and signing in approves that code. Someone who gets a
  household member to open a link with their own code gets a session as that member. The
  fix — a confirmation page that shows the code before approving — is
  [issue #3](https://github.com/be-nj/jellyfin-sso/issues/3).
- **No rate limit on login starts.** Each `/sso/oidc/start` allocates a pending state for
  ten minutes. Bounded in time, not in count.
- **The plugin cannot restrict who a provider admits.** That gate belongs at the provider
  (Authentik policies, Keycloak client scopes, Authelia access rules). Providers without
  per-application assignment — Google among them — therefore admit anyone with an
  account, which is why they are not documented as supported.

## How the claims are kept honest

- 109 unit tests and 49 browser-driven end-to-end tests run on every push.
- Every release signs in through real Authentik, Keycloak and Authelia containers.
- CodeQL runs on every push; no alert is open.
- The defects found in this plugin are filed as issues with the test that now covers them,
  including the ones found by its own author.
