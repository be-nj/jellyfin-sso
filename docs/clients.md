# Phones, tablets and TVs

Back to the [documentation index](README.md) · [README](../README.md)

The Jellyfin apps cannot show a provider's login page, so they use Jellyfin's **Quick
Connect**: the app shows a six-character code, and an already signed-in browser approves
it.

With this plugin the browser does not even have to be signed in:

1. Enable Quick Connect in Jellyfin: Dashboard → **General → Quick Connect**.
2. In the app, choose *Quick Connect* and note the code.
3. On a phone or laptop, open
   `https://<your-jellyfin>/sso/oidc/start/qc?code=<the-code>`. You sign in at your
   provider, and the app logs in by itself. With several providers enabled this asks which
   one first, carrying the code along.

Once approved, the app holds a normal Jellyfin session and stays signed in like any other
login — this is a one-time dance per device, not a weekly ritual.

Yes, that URL is a mouthful, and it has to be done once per device and household member.
Showing the code as a **QR code** on the TV would fix it, and that has to happen in the
Jellyfin client apps themselves — a server plugin cannot draw on their screens. It is on
the [roadmap](../README.md#contributing) as work to be done upstream, not something this plugin
can solve alone.

The reasoning behind using Quick Connect at all is in
[ADR 0002](adr/0002-native-clients-via-quick-connect.md).
