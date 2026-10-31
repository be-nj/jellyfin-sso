# Phones, tablets and TVs

[Documentation index](README.md) · [README](../README.md)

Jellyfin's API has no idea what SSO is. A client asks for a username and a password, or
it asks for a Quick Connect code; there is no third thing it can ask for, and no way for
this plugin to tell a client that a provider exists. So an app signs you in one of two
ways, and which one it is depends on whether the app is the web client in disguise. A
few manage neither, and with those an SSO account cannot sign in at all.

## In the app: the web client, loaded from the server

The official Android app, Jellyfin Desktop, the LG webOS app and any browser all load the
web client **from the server**. The plugin writes its button into that page, so the
provider's login opens where you already are, and nothing else is needed.

"From the server" is the part that matters, and it is not the same as "is a web client".
The webOS app is a small wrapper that reads the server's manifest and navigates to
`<server>/web/…`, so the page and the button come from the same place. The Samsung Tizen
app **packages its own copy** of jellyfin-web into the installable, and runs it from a
local origin. Branding still reaches it, because branding is an API call, so the button
renders — and then submits to a path on the television rather than on the server, which
goes nowhere ([issue #118](https://github.com/be-nj/jellyfin-sso/issues/118)). Samsung
owners are on the Quick Connect route below until that is fixed.

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

## Which apps, and how they were chosen

Nobody publishes install figures for Jellyfin clients, and the lists that rank them rank
reader interest rather than use. So this is not a popularity table. It is every client
that turned up in the ecosystem lists or has a notable number of stars, sorted by the
only thing that matters here: how a login can happen at all.

| App | Platform | Route | Checked |
|---|---|---|---|
| Jellyfin (official) | Android | in the app | 2.7.3, Android 16 emulator — signed in through the button on the login page |
| Jellyfin Desktop | Windows, macOS, Linux | in the app | from its sources — it embeds the web client |
| Jellyfin for LG (webOS) | LG TVs | in the app | from its sources — it navigates to `<server>/web/…`, so the button is the server's |
| Jellyfin for Samsung (Tizen) | Samsung TVs | Quick Connect | from its sources — it packages jellyfin-web and runs it locally, so the button renders but submits nowhere ([#118](https://github.com/be-nj/jellyfin-sso/issues/118)) |
| Jellyfin Android TV | Android TV, Google TV | Quick Connect | 0.19.10, Google TV 14 emulator — signed in |
| Findroid | Android | Quick Connect | 1.2.0, Android 16 emulator — signed in; also against a live server with a real Authentik |
| Streamyfin | Android, iOS | Quick Connect | 0.55.2, Android 16 emulator — signed in |
| Finamp (music) | Android, iOS | Quick Connect | 1.0.2, Android 16 emulator — signed in |
| Moonfin | Android, Android TV, iOS, desktop | Quick Connect | 3.0.0 TV build, Google TV 14 emulator — signed in, and it draws a QR code beside the digits |
| Swiftfin | iOS, tvOS | Quick Connect | from its sources |
| plezy | Android, iOS, desktop | Quick Connect | from its sources |
| Wholphin | Android TV | Quick Connect | on a Google TV, by the maintainer: *Add user* shows a Quick Connect code straight away, with no password screen first; the login was done in a browser on a PC and the television was signed in two or three seconds later. Its builds are ARM-only, so no emulator here can run it — version not recorded |
| Switchfin | PC, PS4, PS Vita, Switch | Quick Connect | from its sources |
| Blink | desktop (Tauri) | Quick Connect | from its sources |
| Jellyfin for Roku | Roku | Quick Connect | from its sources |
| Jellyfin for Kodi | Kodi | Quick Connect | from its sources |
| Streamyfin (TV build) | Android TV | Quick Connect | not run here: ARM libraries, x86 emulator. Same login code as its phone build |
| **tsukimi** | Linux (GTK) | **neither** | from its sources: it authenticates by username and password and has no Quick Connect, so an SSO account cannot sign in to it at all |

Wholphin was checked on real hardware instead, against a live server, because nothing
here can run it: the Android emulator refuses a foreign CPU architecture on an x86 host
("Avd's CPU Architecture 'arm64' is not supported by the QEMU2 emulator"), and the
x86 images that once carried ARM translation no longer do. Its ARM-only builds are not a
shortcoming — televisions are ARM.

Closed-source clients — Infuse, Symfonium — could not be read, and are not listed.
Whether they work comes down to the same question: do they offer Quick Connect.

The emulator checks ran in October 2026 against a throwaway server with this plugin
installed, built by `make stack` — a Jellyfin container with the release ZIP, the suite's provider,
and nothing a person has to type a password into. The server recorded each login as a
session for the same SSO account, which is the thing worth proving: the client never
learns a password, and the account it gets is the one the provider named.

## What a client would need for a provider page of its own

The only third-party client found that integrates with a Jellyfin SSO plugin at all is
[Prismfin](https://github.com/JortWillemsen/prismfin), and it is written against
[9p4's SSO-Auth](https://github.com/9p4/jellyfin-plugin-sso) rather than this plugin: it
calls `/sso/OID/start/{provider}`, lets the callback land on `/sso/OID/redirect/{provider}`,
and then posts the authorized state to `/sso/OID/Auth/{provider}` to receive a Jellyfin
`AuthenticationResult`. This plugin serves none of those paths.

They could be served — the first two are this plugin's own endpoints under another name,
and the third is a session minted from a state the callback has already authorized, which
is what the Quick Connect path does by another route. It will not be:
[#115](https://github.com/be-nj/jellyfin-sso/issues/115) was closed on the judgement that
one client with a handful of users is not worth a second public API, whose one new piece
would be an endpoint that mints a Jellyfin session — the last part of this plugin that
should grow surface it does not need.
