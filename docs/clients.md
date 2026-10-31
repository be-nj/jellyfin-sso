# Phones, tablets and TVs

[Documentation index](README.md) · [README](../README.md)

Jellyfin's API has no idea what SSO is. A client asks for a username and a password, or
it asks for a Quick Connect code; there is no third thing it can ask for, and no way for
this plugin to tell a client that a provider exists. So an app signs you in one of two
ways, and which one it is depends on whether the app is the web client in disguise.

## In the app: the web client in a frame

The official Android app, Jellyfin Desktop, the Samsung and LG apps, and any browser all
show the server's own web client. The plugin writes its button into that page, so the
provider's login opens where you already are, and nothing else is needed.

## Quick Connect: the code route

Native apps — Findroid, Streamyfin, Finamp, Swiftfin, Jellyfin Android TV, plezy — draw
their own login screens, and cannot show a provider's page. They all support Quick
Connect, which this plugin can complete:

1. Enable Quick Connect: Dashboard → **General → Quick Connect**.
2. In the app, choose *Quick Connect* and note the code.
3. On a phone or laptop, open
   `https://<your-jellyfin>/sso/oidc/start/qc?code=<the-code>` and sign in at your
   provider. The app logs in by itself. With several providers this asks which one first,
   carrying the code along.

Afterwards the app holds a normal Jellyfin session — once per device, not per evening,
and no password is ever typed into a television.

A **QR code** on the TV would remove the typing, but it has to be drawn by the client
apps themselves; a server plugin cannot. It is on the [roadmap](../README.md#contributing)
as upstream work. Why Quick Connect at all:
[ADR 0002](adr/0002-native-clients-via-quick-connect.md).

## What was checked, and how

| App | Version | Where | Route | Result |
|---|---|---|---|---|
| Jellyfin (official) | 2.7.3 | Android 16 emulator | in the app | signed in through the button on the login page |
| Jellyfin Android TV | 0.19.10 | Google TV 14 emulator | Quick Connect | code approved by the SSO login, app signed in |
| Findroid | 1.2.0 | Android 16 emulator | Quick Connect | signed in; also against a live server with a real Authentik |
| Streamyfin | 0.55.2 | Android 16 emulator | Quick Connect | signed in |
| Finamp | 1.0.2 | Android 16 emulator | Quick Connect | signed in |
| Streamyfin (TV build) | 0.55.2 | Google TV 14 emulator | — | not checked: the build ships ARM libraries and the x86 emulator cannot load them. Same login code as the phone build |
| Swiftfin, plezy, Jellyfin Desktop | — | read from their sources | — | Quick Connect (Swiftfin, plezy), web client (Desktop) |

October 2026, against a throwaway server with this plugin installed, built by
`make stack` — a Jellyfin container with the release ZIP, the suite's provider, and
nothing a person has to type a password into. The server recorded the logins as sessions
for the same SSO account, which is the thing worth proving: the client never learns a
password, and the account it gets is the one the provider named.

## What a client would need for a provider page of its own

The only third-party client found that integrates with a Jellyfin SSO plugin at all is
[Prismfin](https://github.com/JortWillemsen/prismfin), and it is written against
[9p4's SSO-Auth](https://github.com/9p4/jellyfin-plugin-sso) rather than this plugin: it
calls `/sso/OID/start/{provider}`, lets the callback land on `/sso/OID/redirect/{provider}`,
and then posts the authorized state to `/sso/OID/Auth/{provider}` to receive a Jellyfin
`AuthenticationResult`. This plugin serves none of those paths.

They could be served — the first two are this plugin's own endpoints under another name,
and the third is a session minted from a state the callback has already authorized, which
is what the Quick Connect path does by another route. It is not built, because one client
with a handful of users is thin ground for a second public API that would then have to
keep working, and because the endpoint that mints a session is the last part of this
plugin that should be improvised. It is written down as
[issue #115](https://github.com/be-nj/jellyfin-sso/issues/115), to pick up if another
client adopts the same shape.
