# Native clients supported via Quick Connect

We support native Jellyfin apps (TV, mobile), not only the web client. Because the
**Ephemeral Secret** is produced in the external browser doing the OIDC redirect and
cannot be handed to a native app directly, native logins go through Jellyfin's Quick
Connect: the browser approves a code, the app polls for it.

This couples native SSO to Quick Connect being enabled server-side — a constraint not
visible in the plugin code. Accepted because web-only would be a showstopper for a
household with TV/mobile apps.
