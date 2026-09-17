# IdP groups are the single source of truth for library access (declarative sync)

Library access is derived declaratively from the user's IdP **Groups**. On every login
the plugin computes the full set of allowed libraries from the current groups and
*overwrites* Jellyfin's per-user `EnabledFolders`. Group changes in the IdP propagate
automatically on next login; manual edits in Jellyfin's user UI are reverted.

We rejected an additive/advisory model (plugin only grants, never revokes): it drifts
into exactly the per-user permission sprawl SSO is meant to eliminate, and makes the
effective state hard to reason about. A per-user "exclude from sync" opt-out (hybrid)
may be added later if a real exception appears, but the default is strict declarative.

## Consequences

- Admins must manage library access through Group Mappings, not Jellyfin's user UI.
- A user removed from a group loses access on their next login, not immediately
  (revocation latency = until next login / token refresh — to be revisited).
