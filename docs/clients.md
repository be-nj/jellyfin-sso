# Phones, tablets and TVs

[Documentation index](README.md) · [README](../README.md)

Jellyfin's apps cannot show a provider's login page, so they use **Quick Connect**: the
app shows a six-character code and another device approves it. With this plugin that other
device does not have to be signed in:

1. Enable Quick Connect: Dashboard → **General → Quick Connect**.
2. In the app, choose *Quick Connect* and note the code.
3. On a phone or laptop, open
   `https://<your-jellyfin>/sso/oidc/start/qc?code=<the-code>` and sign in at your
   provider. The app logs in by itself. With several providers this asks which one first,
   carrying the code along.

Afterwards the app holds a normal Jellyfin session — once per device, not per evening.

A **QR code** on the TV would remove the typing, but it has to be drawn by the client apps
themselves; a server plugin cannot. It is on the [roadmap](../README.md#contributing) as
upstream work. Why Quick Connect at all:
[ADR 0002](adr/0002-native-clients-via-quick-connect.md).
