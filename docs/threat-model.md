# Threat model

[Documentation index](README.md) · [README](../README.md) · [SECURITY.md](../SECURITY.md)

What this plugin trusts, what it does not, and which test holds each claim. Written so a
reader who trusts neither the author nor a model can check the claims one by one.

## What is trusted

- **The identity provider**, for who a person is and which groups they hold. The plugin
  verifies that the statement came from that provider and was meant for this server; it
  cannot judge whether the provider's answer is correct. With several providers, each is
  trusted for the groups of the mappings that apply to it, and a mapping on *All
  providers* applies to every one of them.
- **TLS to the provider.** Discovery and JWKS are fetched over the configured issuer URL, and the discovery document cannot move them elsewhere: its issuer has to match the authority, and its keys, token, userinfo and end-session endpoints — the ones the plugin hands something to, the last of them a user's own ID token — have to be on the authority's own host. Redirects are not followed, so none of those calls can be moved after the check. The authorize endpoint is not among them, and the reason is at the call: nothing is handed to it, and where a provider puts the form people type into is its own arrangement. The paths are the provider's business — Authentik's differ from its issuer by design — the host is not. A provider that documents a layout across several hosts, as Google does, is admitted those hosts only when an administrator names them on the provider card (*Further endpoint hosts*, empty by default). That moves the trust from the document to the person who configured the provider, which is where the issuer URL's own trust comes from; it also means a host named there is trusted exactly as far as the issuer's.
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
| Forged ID token | Signature checked against the provider's JWKS, with the acceptable algorithms named and asymmetric: a JWKS is public, so a symmetric key in one must not be able to decide whether a token is genuine | `A_token_signed_by_an_untrusted_key_is_rejected`, `An_unsigned_token_is_rejected`, `A_token_signed_with_a_symmetric_key_from_the_key_set_is_rejected` |
| A key the provider did not publish for signing | Only keys marked for signatures, or marked not at all, are offered to the validator; a key set carries encryption keys too, and `use` is what distinguishes them (RFC 7517 §4.2) | `A_key_published_for_encryption_is_not_offered`, `A_key_set_with_only_encryption_keys_is_refused` |
| An ID token with no issued-at time | `iat` is required, must be a plausible time, and must not claim to come from the future. How old the token may be is left to `exp`, which is required too. Found by the conformance suite: nothing required it, and neither of this repository's fake providers minted one | `A_token_with_no_issued_at_time_is_refused`, `A_token_issued_in_the_future_is_refused`, `An_implausible_issued_at_time_is_refused_as_a_token_problem` |
| A token signed with an algorithm the provider was not configured for | Optional: *Signing algorithms* on the provider card pins what that provider may sign with, on the ID token and the logout token alike. Empty by default, which accepts the whole asymmetric set; the setting can only narrow it, so naming `HS256` or `none` leaves the default in place. Not a forgery defence — every key in the set is the provider's — but it takes away the choice of a weaker one. Found by the conformance suite | `A_provider_pinned_to_one_algorithm_refuses_a_token_signed_with_another`, `A_logout_token_signed_with_an_unexpected_algorithm_is_refused`, `A_symmetric_algorithm_cannot_be_configured_back_in` |
| Token minted for another application | `aud` must name this provider's client id, and where it names several, `azp` must name this one — a provider can be configured to cross audiences, and such a token was issued for the other client | `A_token_for_another_client_is_rejected`, `A_token_naming_several_audiences_without_azp_is_rejected` |
| Token from a different provider | `iss` must equal the issuer from discovery | `A_token_from_another_issuer_is_rejected` |
| Replayed token from an earlier login | `nonce` must equal the one this login issued | `A_replayed_token_from_another_login_is_rejected`, `A_token_without_a_nonce_is_rejected` |
| Expired token | Lifetime checked, 60 s skew | `An_expired_token_is_rejected` |
| Replayed callback (`state`) | `state` is single-use and carries its own provider and redirect URI | `A_replayed_callback_state_is_refused` |
| A code from one provider redeemed through another (mix-up) | Each provider card has its own callback and its own `state`, so a code delivered with another provider's state is refused before it is redeemed anywhere. Where the honest provider accepts every callback of this server — a registration by pattern — that is not enough, and RFC 9207 closes it: an `iss` on the way back must be the issuer of the provider the login started at, and a provider whose discovery says it sends `iss` must send it. Checked before the token request, so the code and the PKCE verifier never reach the wrong token endpoint (#325) | `A_code_that_comes_back_with_another_providers_state_is_never_redeemed`, `A_code_the_honest_provider_issued_is_not_sent_to_another_providers_token_endpoint`, `AuthorizationResponseIssuerTests` |
| Stolen authorization code | PKCE S256; the verifier never leaves the server | `PendingStateStoreTests` |
| A login's code delivered to another host | Optional: *Public address* fixes the redirect URI the provider is given. Without it that URI is built from the request's `Host` and forwarded headers, and a provider that accepts a redirect URI on any host — a wildcard, a pattern — sends the code to whichever host a forged header named. State, nonce and PKCE do not stop that: they all belong to the flow the attacker started. A provider that accepts only the exact redirect URI closes it as well, which is why the setting is optional. A stored value that is no address stops the login instead of handing the decision back to the headers | `With_a_public_address_a_forged_host_reaches_neither_the_provider_nor_a_redirect`, `A_forged_host_does_not_reach_the_redirect`, `A_stored_value_that_is_no_address_stops_the_login_and_says_why`. A forwarded host or public address is accepted only when it is a host name or address `System.Uri` recognises with a port from 1 to 65535, and a public address that cannot be read is refused in words instead of throwing (#327): `RedirectGuardPropertyTests` (every accepted value reads back as the same origin), `A_host_that_is_not_an_address_is_not_taken_from_the_header`, `A_public_address_with_an_unconvertible_host_is_refused_not_thrown`, `A_public_address_whose_host_is_not_a_host_name_is_refused` |
| Stranger registers an existing username at the provider | Accounts are keyed by `(provider, sub)`; a colliding name is refused until an administrator links it | `An_unlinked_identity_cannot_take_over_an_existing_account` |
| A name that reads like an existing account's | Before the collision check, a provider's name loses its padding and every Unicode default-ignorable code point, and is composed (NFC): a combining grapheme joiner after `admin` passed Jellyfin's own name rule and made a second account reading `admin` (#324). Letters from another script that resemble Latin ones are not handled | `A_name_that_reads_like_the_administrators_does_not_become_a_second_account`, `A_preferred_username_loses_the_characters_that_draw_nothing` |
| Reusing the one-time login secret | Single-use, 90 s, bound to the user ID and username; rejected after account revocation | `An_ephemeral_secret_works_once`, `An_ephemeral_secret_only_works_for_its_own_user`, `Reusing_a_deleted_accounts_username_does_not_reuse_its_secret` |
| Anonymous call to the admin API | Every data endpoint requires an administrator | `The_admin_api_is_closed_to_anonymous_callers` |
| Group rename at the provider silently keeping access | Permissions are rewritten from the groups on every login | `Access_is_revoked_when_the_groups_disappear` |
| Mistyped admin group locking everyone out | The last administrator is never demoted, and the check and the write that acts on it are one turn: two administrators signing in at the same moment could otherwise each see the other as the one who would be left. A disabled administrator does not count as the one who remains (#341) | `PermissionSyncTests`, `The_last_two_administrators_cannot_both_be_demoted_at_once`, `A_disabled_administrator_does_not_count_as_the_one_that_remains` |
| A provider configured to let in more people than it should | Optional: *Refuse logins that match no mapping* turns away a login that matches no group mapping, before an account exists, and keeps doing so for accounts that already exist. Off by default, because the provider is the usual place for this decision; on, it is a second lock that does not depend on the provider staying right | `LoginGateTests`, `A_login_in_no_mapped_group_is_refused_once_the_server_asks_for_one`, `An_account_that_loses_its_last_group_loses_the_login_too` |
| A group name or subject from the provider emptying the configuration | Jellyfin writes a plugin's configuration without checking its characters and replaces a file it cannot read back with an empty one, so a control character in anything stored cost every provider, mapping and account link at the next restart. A group name with one is not remembered, a login whose subject has one is refused, a save refuses one in any text, and as the last line the plugin replaces such characters on the way to the file | `A_group_name_with_a_control_character_leaves_a_file_the_server_can_read_back`, `An_identity_whose_subject_the_file_cannot_hold_is_refused_before_anything_is_stored`, `The_plugin_scrubs_what_it_saves`, `A_control_character_is_written_to_the_file_and_cannot_be_read_back` |
| Group names from the provider growing the configuration file | The remembered names are capped at 500 and at 256 characters each; a longer name is still matched against the mappings, it is only not offered as a suggestion. The count alone let one login write four megabytes (#323) | `Group_names_of_many_kilobytes_do_not_grow_the_configuration_file_by_megabytes`, `A_group_name_longer_than_the_cap_is_not_remembered`, `But_it_still_matches_a_mapping_of_that_name` |
| A subject or name changed by the configuration file | A carriage return is written to the file as it is and read back as a line feed, so a link keyed on a subject holding one stopped matching. It is not storable: the save refuses it and the scrubber replaces it (#329) | `ConfigurationRoundTripPropertyTests` (everything the rules accept comes back unchanged; everything scrubbed is storable), `A_carriage_return_is_either_refused_or_survives_the_file` |
| A group the provider never sent, or a mapping with no group, granting access | Only non-empty JSON strings are groups on the ID-token path, as on the userinfo path: null, numbers, objects and nested arrays are not. A mapping with a blank group is refused on save, in words, by the settings page and by the server, and is never matched (#330) | `A_group_the_provider_did_not_send_as_a_string_is_not_a_group`, `A_token_without_groups_matches_no_mapping`, `A_group_mapping_without_a_group_is_refused_in_words_and_never_matched`, `ClaimsExtractorPropertyTests` |
| A second provider granting the groups of the first, administrator included | Each group mapping can be limited to the providers whose logins it applies to (`ProviderIds`, *Providers* on the card). A login through any other provider does not match it, even with a group of exactly that name: not for the administrator flag, not for libraries or permissions, and not for *Refuse logins that match no mapping*. The log names the mapping it did not match. A save naming a provider that does not exist is refused, since dropping the id would leave an empty list, and an empty list means every provider. That is also the default, so a mapping saved before the choice existed behaves as it did — and an administrator mapping left on *All providers* trusts every provider that sets permissions with the administrator flag (#340) | `A_mapping_for_one_provider_does_not_make_an_administrator_through_another`, `A_mapping_for_one_provider_grants_no_libraries_through_another`, `Refuse_logins_that_match_no_mapping_refuses_the_other_provider`, `A_mapping_that_names_no_provider_still_applies_to_every_provider`, `A_mapping_limited_to_a_provider_that_does_not_exist_is_refused`, `The_provider_list_comes_back_from_the_configuration_file` |
| An account named with padding or invisible characters when the token has no name | The subject that stands in for a missing name goes through the same rule as any name: trimmed, default-ignorables removed, NFC (#331) | `The_username_that_stands_in_for_a_missing_name_is_cleaned_like_any_other`, `A_subject_with_nothing_visible_cannot_name_an_account` |
| Claims smuggled under another name | Only the configured groups claim is read | `Claims_under_a_different_name_are_not_read_as_groups` |
| Forged log lines through a crafted provider id | Only configured ids and the fixed RFC 6749 error codes are logged | `LoggableTests` |
| Provider name injecting markup into the login page | Names are HTML-escaped, ids URL-encoded | `A_provider_name_cannot_inject_markup` |
| Someone else's identity linked to your account | A link to an identity another account holds is refused, and the refusal is decided under the same lock that writes it — two callbacks finishing together would otherwise both have read an account as free | `SessionAndLinkingTests`, `SelfServiceLinkTests`, `Two_accounts_claiming_one_identity_leave_one_owner` |
| A userinfo answer about somebody else deciding your groups | The `sub` in a userinfo response must match the ID token's, as OIDC Core §5.3.2 requires; where it does not, the answer is not used | `A_userinfo_answer_about_somebody_else_is_not_used` |
| An unlinked account keeping tokens nobody can end | Both unlinks — the user's own and the administrator's — end that account's sessions, because back-channel logout finds an account through its link and nothing else. Nothing is removed before a revocation has succeeded, so a revocation that fails, or a request that is cancelled, leaves a state the caller can retry rather than a session nothing can reach | `UnlinkingTests`, `Unlinking_ends_the_sessions_it_would_otherwise_orphan`, `An_administrators_unlink_ends_the_sessions_too` |
| Your identity linked to someone else's account | A login can only be finished in the browser that started it: a cookie is minted when the flow begins and has to come back with the callback. Without that, an account holder could ask for an authorize URL carrying their own user id and get somebody in an administrator group to complete it — the link would attach the victim's subject to the asking account, and the victim's next login would write their groups, administrator flag included, onto it | `A_link_flow_finished_in_another_browser_is_refused`, `A_state_presented_by_another_browser_is_refused` |
| A login completed in your browser by somebody else | The same cookie. A stranger holding a `code` and a `state` cannot have your browser spend them, so they cannot sign you in as themselves | `A_login_callback_opened_in_another_browser_is_refused` |
| Forged back-channel logout ending a session | The logout token is checked against the provider's JWKS, issuer and audience | `A_token_signed_by_nobody_the_provider_knows_is_refused`, `A_token_signed_by_somebody_else_is_rejected` |
| Forged back-channel logout tokens leaving logins without keys | The endpoint is anonymous, and a token naming an unknown key makes the plugin fetch the provider's keys again, in case they were rotated. The cached document stays until a new one has arrived, and tokens posted at the same time share one fetch, so a stream of them cannot empty the document logins use (#316). Each still costs the provider one fetch, held to the per-address rate limit | `A_failed_refresh_keeps_the_document_logins_use`, `Refreshes_asked_for_at_the_same_time_share_one_fetch` |
| An ID token replayed as a logout token | A logout token must carry the logout event and must not carry a `nonce` | `An_id_token_cannot_be_replayed_as_a_logout_token`, `A_token_without_the_logout_event_is_rejected` |
| A captured logout token replayed | Each accepted token is remembered for longer than it could stay valid and refused the second time, and `iat` is required and may be at most five minutes old in any case | `A_token_captured_earlier_cannot_be_replayed_later`, `The_same_logout_token_cannot_be_used_twice` |
| A stolen logout handle ending somebody's provider session | The cookie holds an opaque 32-byte handle, never the token; it is `HttpOnly`, single-use, scoped to `/sso/oidc`, and `Secure` whenever the server's external address is `https`. Worst case for an interceptor on a plain-HTTP deployment is ending a session that was about to end. Nothing is kept while *Sign out of the identity provider too* is off, and what is kept is in memory only, for at most 30 days or 10,000 browser logins | `LogoutSessionStoreTests`, `Nothing_is_retained_while_the_provider_logout_is_switched_off` |
| An ID token sent as a hint to the wrong provider | The retained login records the provider id, authority and client id, and the hint is sent only when all three still match | `A_token_is_not_used_for_a_different_provider_or_changed_configuration`, `Logout_does_not_send_a_retained_token_after_the_client_id_changes` |
| A pending login restoring access after revocation | Every credential a login hands out carries the revocation version from the start of that login and dies with a later revocation of its account or identity. Tokens Jellyfin mints itself — password, SSO secret and Quick Connect approvals — are checked after they exist and taken back when a revocation landed in between; a revocation never waits for a login | `SecurityRegressionTests`, `NativeLoginLifetimeTests`, `ReviewSecurityTests` |
| A flood of logins delaying a revocation | Revocation records its versions before it awaits anything, and its sweep waits only for another sweep of the same account. The first version of the revocation guard held one lock around every native login and was replaced for this | `A_back_channel_logout_does_not_wait_for_a_login_in_progress` |
| Links removed without revoking their sessions | Links are never taken from a submitted configuration — not from the plugin's admin page and not through Jellyfin's generic plugin-configuration endpoint; only the unlink paths remove them, and those revoke first | `A_whole_configuration_saved_through_jellyfin_keeps_the_live_links`, `Jellyfins_own_configuration_endpoint_cannot_remove_a_link_without_revoking_it` |
| A failed first login leaving a passwordless account | The first database commit is disabled and SSO-only; failed setup removes the provisional account where possible | `SsoUserPersistenceTests`, `A_failed_initial_policy_write_removes_the_provisional_account` |
| An avatar update restoring old account rights | The persistence operation writes image data only | `An_image_write_preserves_account_permissions_and_authentication`, `Avatar_download_must_not_undo_administrator_disabling_account` |
| Linking while JIT provisioning is awaiting storage | All link writers check uniqueness under the configuration lock at the final write; a losing provisional account is removed | `Jit_and_self_service_link_must_not_create_two_owners_for_one_identity` |
| Discovery naming a foreign JWKS host | Endpoint validation precedes the JWKS fetch, with shared paths permitted only on the authority's origin | `Discovery_must_check_the_jwks_host_before_sending_the_request` |
| A provider's authorization endpoint running script on Jellyfin's origin | The authorization endpoint may be on any host, but it must be an absolute http or https address without user information or fragment, checked when discovery is accepted and again before the URL is built; the link page navigates only to an http or https address (#339) | `An_authorization_endpoint_a_browser_should_not_be_sent_to_is_refused` |
| A plugin page framed under a decoy, or a refusal sniffed into markup | Every answer under /sso carries `X-Frame-Options: DENY`, `frame-ancestors 'none'` and `X-Content-Type-Options: nosniff` (#346) | `Every_controller_of_the_plugin_carries_the_headers`, `A_page_may_not_be_framed_or_read_as_another_type` |
| A provider that admits everyone creating an account for everyone | Per provider, *Create an account at first login* off: an identity without a link is refused before its name is looked at, and no account or link is written — also when a stale link was cleared on the way. Off for a provider added on the page; on for one saved before the switch existed, so an upgrade locks nobody out | `An_unknown_identity_gets_no_account_where_the_provider_creates_none`, `A_stale_link_does_not_become_a_way_to_create_an_account`, `The_refusal_says_nothing_about_which_names_exist`, `A_provider_saved_before_the_switch_existed_still_creates_accounts`, `A_provider_that_creates_no_accounts_signs_in_only_linked_identities` |
| A provider without groups stripping accounts, or its switch opening the group gate for everyone | *Set permissions from this provider's groups* off writes nothing to the account at a login through that provider, and exempts only that provider from *Refuse logins that match no mapping*; every other provider is still held to it. What an account then has is what an administrator gave it by hand; a stranger there gets in only where *Create an account at first login* is also on, and then to an account without libraries, Live TV or channels — Jellyfin turns those two on for every new user, the plugin turns them off (#317) | `A_new_identity_is_created_linked_and_restricted_to_sso`, `A_provider_that_does_not_manage_permissions_leaves_the_account_alone`, `The_rule_does_not_apply_to_a_provider_that_does_not_manage_permissions`, `The_rule_still_applies_to_a_provider_that_does_manage_them`, `A_provider_without_groups_leaves_a_linked_account_as_it_was` |
| The provider check used to make the server fetch an address | It takes a provider id and reads the address from the saved configuration; the request carries none. Administrators only. It fetches what a login for that provider fetches, under the same discovery rules, and a confidential client's secret is read to see that it exists and is in no line of the answer | `Checking_a_provider_reports_what_a_login_would_find`, `A_confidential_clients_secret_is_found_without_being_repeated`, `A_provider_without_an_issuer_is_told_so_and_nothing_is_fetched` |
| The list of refused sign-ins steering an administrator to the wrong account | The list offers a subject and a provider to link, never an account: the name beside them comes from the provider, is shown as text, and is not matched against Jellyfin's users. Fifty entries and a day at most, in memory, administrators only — a provider that admits everybody can fill it, not grow it | `The_list_never_grows_past_its_capacity_and_keeps_the_newest`, `A_name_is_cut_to_a_label_and_an_unusable_subject_is_not_listed`, `A_refused_sign_in_is_linked_from_the_account_links_tab`, `A_provider_that_creates_no_accounts_signs_in_only_linked_identities` |
| A host list that admits more than it names | *Further endpoint hosts* takes host names, compared whole and with their port: no wildcard, no scheme, no suffix match, and an entry that is not a host name admits nothing. Naming one host does not admit another, and a host taken off the list is refused at once rather than when the cached document expires | `A_lookalike_of_a_named_host_is_refused`, `An_entry_that_is_not_a_host_name_admits_nothing`, `Naming_one_host_does_not_admit_the_others`, `A_host_taken_off_the_list_is_not_served_from_the_cache`, `A_provider_serving_keys_and_tokens_from_a_named_host_signs_in` |
| A logout for one account signing out another | The subject is matched by `(provider, sub)`, as a login is | `Another_users_logout_leaves_this_session_alone`, `SessionRevokerTests` |
| Flooding the anonymous login endpoints | 60 requests per minute per address, answered with 429 and a Retry-After; the count has an end of its own | `A_caller_past_the_limit_is_turned_away_and_told_when_to_come_back`, `LoginRateLimiterTests` |
| A picture claim, or a brand document, pointing the server at something else | One fetcher for all of them, documents included: absolute `http(s)` only, at most three redirects **with every hop checked, not just the first**, a size counted while the body arrives rather than read off a header, and only the image types Jellyfin's own pipeline reads — SVG is a document that can carry script, and is refused. A logo is fetched only from the provider's own host. A picture may come from anywhere public, and from the provider's host, but not from a private or loopback address unless the operator turns that on. Nothing that comes back can fail a login, and the whole fetch has one deadline — headers, redirects and body, because the body used to have none | `OutboundAddressesTests`, `RemoteImageDeadlineTests`, `A_redirect_to_somewhere_else_inside_the_network_is_not_followed`, `A_picture_that_is_not_an_image_is_refused_and_the_login_still_works`. The address judge is also held against an independent table of the IANA special-purpose blocks over generated addresses in every spelling `Uri` accepts, and refuses Teredo, the IPv6 benchmarking range and the whole of local-use NAT64 (#328): `OutboundAddressesPropertyTests`, `A_Teredo_address_is_not_public`, `The_IPv6_benchmarking_range_is_not_public`, `A_local_use_NAT64_address_is_not_judged_by_the_wrong_four_bytes` |
| Filling the server's memory with abandoned logins | Pending states expire after ten minutes and are capped at 10 000 | `PendingStateStoreTests` |
| A provider that answers slowly, endlessly or with redirects | Every call to the provider — discovery, keys, token, userinfo — has one 20-second deadline covering the body, reads at most 1 MiB, and follows no redirect. A deadline or an oversized answer is a refusal in words, not a 500: the timeout used to escape the login page as one (#321), and the answers had no size cap at all (#322). A failure is not cached; the next login fetches afresh | `A_discovery_document_that_trickles_in_is_given_up_on_and_refused_in_words`, `A_provider_answer_of_many_megabytes_is_refused`, `A_misbehaving_endpoint_is_refused_and_leaves_nothing_behind`, `ProviderAnswerTests` |

## Known gaps

- **This model was written by the same model that wrote the code**, and two reviews of the
  whole plugin have found things it had not accounted for: thirteen in early November 2026 —
  one of them a way for any account holder to end up an administrator
  ([#126](https://github.com/be-nj/jellyfin-sso/issues/126)) — and twelve more on the 5th,
  including a host check this document claimed that the code had been written for, tested
  for, and never actually called
  ([the hardening review](reviews/2026-11-05-hardening.md)). An outside review the same day
  — the first by something that did not write this plugin — found eight more, two of them
  invariants this document asserts that held only as long as nothing happened at the same
  time as anything else ([the outside review](reviews/2026-11-05-outside-review.md)). Every one is fixed and tested,
  and the rows above were corrected where they had claimed more than the code did.

  The day after, the OpenID Foundation conformance suite was run against the plugin for
  the first time — the other side of the protocol, sending the broken responses rather
  than reasoning about them — and found three more, one of them a required claim that
  nothing checked and that neither of this repository's own fake providers ever minted
  ([the conformance run](reviews/2026-11-06-oidc-conformance.md)). That is the honest
  status of this document: it is a record of what has been thought about, not evidence
  that enough has been.
- **API keys are outside revocation.** Revoking an account ends its device tokens.
  API keys from Dashboard → **API Keys** belong to the server in Jellyfin's model, not to
  the administrator who made one, so an administrator revoked at the provider keeps the
  keys they created. The plugin has no record of who made which key and does not try to
  guess. [Security notes](security.md) says to review the list when an administrator
  leaves.
- **A group change waits for the next login.** Removing someone from a group at the
  provider changes their permissions only when they sign in again. A session *ended* at
  the provider now ends in Jellyfin too, where the provider sends back-channel logout
  (Authentik, Keycloak; not Authelia) — see
  [Back-channel logout](login-and-logout.md#back-channel-logout). Changing a group without
  ending the session still needs Jellyfin's own user screen, or ending the session under
  Dashboard → Devices. See [Security notes](security.md).
- **A forwarded header is taken from whoever sent the request.** The plugin reads
  `X-Forwarded-Prefix`, `-Host` and `-Proto` itself rather than through Jellyfin's
  known-proxy handling, so what they are allowed to contain is the whole of the defence:
  a prefix has to look like a path, a host like a host, and the pages escape both again
  for the context they land in. A proxy that strips client-supplied forwarded headers —
  most do not by default — closes this properly. With *Public address* set, the redirect
  URI, the cookies and the pages' links do not come from these headers at all; what is
  left to them is the prefix of what a served page fetches, and whether the browser is
  sent to the public address first.
- **Quick Connect needs the person to approve.** Opening `/sso/oidc/start/qc?code=…` and
  signing in no longer authorizes the code on its own — the login ends on a page that
  names the account and shows the code, and approval is a separate click
  ([#1](https://github.com/be-nj/jellyfin-sso/issues/1)). A household member who is sent
  such a link therefore sees whose code they are about to let in — and that page refuses
  to be framed, because a click can be stolen from a frame and the click is the whole
  approval.
  What is left is the click itself: somebody with a session at the provider who clicks
  Approve without comparing lets the sender's device into their account. *Type the Quick
  Connect code in* closes that — a code in a link is then ignored and the page asks for
  the one on the device, three tries for one sign-in
  (`With_the_code_typed_in_a_code_from_the_link_is_neither_shown_nor_approved`,
  `The_third_wrong_code_ends_the_approval`). It is off by default, and it does not help
  against somebody who is sent a code with the link and types it in: that is the same
  trick Jellyfin's own Quick Connect page is open to.
- **The limit counts addresses, not people.** Everyone behind one router is one caller,
  and a caller with an IPv6 prefix to walk is many. Sixty requests a minute is roomy
  enough that the first case does not bite and tight enough that the second gains little;
  it is a ration on work, not an authentication decision. See
  [Configuration](configuration.md).
- **The plugin cannot restrict who a provider admits.** That gate belongs at the provider
  (Authentik policies, Keycloak client scopes, Authelia access rules). Providers without
  per-application assignment therefore admit anyone with an account. Google is the
  common case: a published *External* OAuth client admits every Google account, and only
  its *Internal* audience or its *Testing* status narrow that. The plugin can be pointed
  at Google since *Further endpoint hosts* exists; the [Google guide](google.md) says how,
  from one login walked by hand, and Google is not in the test matrix. What the plugin can
  decide is whether being admitted is enough for an account: with *Create an account at
  first login* off for a provider, an identity nobody linked gets none, and a provider
  added on the configuration page starts that way.

## How the claims are kept honest

- 594 unit tests and 139 browser-driven end-to-end tests run on every push, against the
  oldest Jellyfin a build is offered to and the two newest released — and against every
  12.x there is once a week.
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
