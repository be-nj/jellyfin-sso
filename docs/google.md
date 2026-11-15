# Google setup

[Documentation index](README.md) · [README](../README.md)

Google works as a provider for this plugin, and it is unlike the others in two ways that
decide how you set it up. Read those first.

This guide was written against one login walked end to end by hand, on 16 November 2026,
with a personal Google account. It is not one of the three providers CI runs on every
change; see [what is tested](#what-is-tested). It needs plugin **1.12.22** or newer.

## Before you start

**Google lets in whoever it knows.** Authentik, Keycloak and Authelia can be told which of
their users may use Jellyfin. Google cannot be told that per person; what it offers is the
audience of the OAuth client:

| Audience and status | Who can sign in |
|---|---|
| *External*, status **Testing** | The test users you list, up to a hundred. Google asks them to consent again every seven days |
| *Internal* | The accounts of your Google Workspace organisation. Not available with a personal account |
| *External*, published | **Anyone with a Google account** |

For a household, *External* in **Testing** with the household's addresses as test users is
the setting that keeps strangers out at Google's end.

**Google sends no groups.** Groups are how this plugin normally decides what somebody may
see, and it takes away what the groups no longer grant. With no groups, that means taking
everything away at every login.

So the two switches on the provider card that exist for exactly this:

- **Create an account at first login: off.** Somebody without a linked account is turned
  away and nothing is created for them. This is the plugin's own lock, and it holds even
  if the Google client is published one day.
- **Set permissions from this provider's groups: off.** A login leaves the account as you
  set it by hand.

## At Google

In the [Google Cloud console](https://console.cloud.google.com/):

1. Create a project.
2. **Google Auth platform → Branding → Get Started**: an app name and a support address;
   audience **External**; a contact address; agree to the data policy; **Create**.
3. **Audience → Test users → Add users**: every Google address that should be able to sign
   in.
4. **Clients → Create client → Web application**. Under **Authorized redirect URIs**, the
   *Redirect URI* the plugin's provider card prints. Google insists on `https://` for
   anything but `localhost`, and on a real domain name: an IP address is refused.
5. Google shows the **Client ID** and the **Client secret**. The secret is shown in full
   this once.

## The plugin's provider card

- *Issuer URL*: `https://accounts.google.com`
- *Client ID*: the one Google showed.
- *Further endpoint hosts*:
  `www.googleapis.com oauth2.googleapis.com openidconnect.googleapis.com` — Google serves
  its keys, its token endpoint and userinfo from those three, not from the issuer's host.
- *Create an account at first login*: **off**.
- *Set permissions from this provider's groups*: **off**.
- *Scopes*: empty.

A Google web client is a **confidential client**: it has a secret, and Google refuses the
login without it. The secret has no field on the page; hand it to Jellyfin as an
environment variable or a file and set the reference through the admin API, as
[Confidential clients](configuration.md#confidential-clients) describes.

Then press **Check saved settings**. It should list Google's issuer, the endpoints, two
signing keys and *Client secret: found*. If it says the discovery document *puts an
endpoint on another host*, the three hosts above are missing or misspelled.

## Letting people in

With account creation off, each person needs a link between their Google account and a
Jellyfin account:

- **They have a Jellyfin account with a password.** They sign in to Jellyfin with it and
  open `/sso/link` on your server. The page a refused Google sign-in shows names that
  address.
- **You do it.** They try to sign in with Google once and are refused; the attempt then
  appears on the **Account Links** tab under *Refused sign-ins*, and **Link…** fills the
  form. Choose the Jellyfin account yourself: the name shown is the display name they
  gave Google.

Libraries and permissions are what you set on the Jellyfin account in the dashboard. They
stay that way.

## What a login looks like

From the walked login:

- The subject Google sends is a 21-digit number, the same for that account every time. It
  is what the link is made on; the e-mail address is not used.
- Google sends no `preferred_username`. Where an account is created, it is named after
  the person's display name at Google, spaces included.
- No groups arrived, and none were asked for.
- The login used the client secret over HTTP Basic, and Google's ID token validated
  against the keys from `www.googleapis.com`.

## What is tested

One login, by hand, with `make google` ([tests/google/](../tests/google/README.md)): a
throwaway Jellyfin 12.0, a Google web client in *External / Testing*, account creation on
for that run so there was something to look at, permissions not managed. It signed in,
created the account, and left its permissions alone. The plugin's log showed no warning.

Not tested against Google, and so resting on the suite's own provider and the three in
CI: linking an existing account, Quick Connect, profile pictures, signing out, a
Workspace *Internal* client, and more than one person.

Two things Google does not do, which the others do:

- **No sign-out at Google.** Google publishes no end-session endpoint, so *Sign out of the
  identity provider too* has nothing to call. Signing out of Jellyfin leaves the browser
  signed in to Google.
- **No back-channel logout.** Google does not tell this plugin when a session ends on its
  side. Removing somebody's test-user entry stops their next login; it does not end the
  Jellyfin session they have. End that in Jellyfin, or remove their link, which does.
