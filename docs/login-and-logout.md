# Login button and logout

[Documentation index](README.md) · [README](../README.md)

## Login button

**Built in:** saving the configuration writes one button per enabled provider into
Jellyfin's *Login Disclaimer*, unless *Put a login button on the login page* is turned off
on the **Sessions** tab — turn it off if you place your own button and do not want a
second one. The buttons sit between `<!-- sso-login-buttons:start -->`
and `:end`; only that part is ever rewritten, so your own text stays, and disabling every
provider removes the block alone.

<a href="img/login-button.png"><img src="img/login-button.png" alt="Jellyfin's login page with a Login with Authentik button" width="520"></a>

**Two looks, per provider.** *Paint the login button in this provider's colour* on the
provider's card decides which one. Off, the button is one of Jellyfin's — the provider's
colour sits on its left edge alone. On, the whole button carries that colour, and the mark
and the label turn black or white, whichever reads on it. With several providers each one
chooses for itself.

<a href="img/login-button.png"><img src="img/login-button.png" alt="The plain button: Jellyfin's surface with an orange edge" width="400"></a>
<a href="img/login-button-branded.png"><img src="img/login-button-branded.png" alt="The branded button: Authentik's orange with a black mark" width="400"></a>

The colour is the provider's own, recognised from its name or issuer — Authentik's orange,
Keycloak's grey, Authelia's and Google's blue, Okta's, Auth0's — and anything unrecognised
gets Jellyfin's blue. Black or white is picked by measuring the colour's luminance, not by
taste, so a light brand does not end up with white text nobody can read.

The whole login, recorded against a real Authentik in the test suite:

<a href="img/login-flow.mp4"><img src="img/login-flow.gif" alt="A login through the button: Jellyfin's login page, Authentik's form, the home screen" width="640"></a>

The button is a small form in Jellyfin's own button classes, so themes style it like the
others. A form rather than a link because jellyfin-web rewrites every link in the
disclaimer to open in a new tab and routes the rest through its own router, which does not
know the plugin's addresses — see [issue 30](https://github.com/be-nj/jellyfin-sso/issues/30).
Under a sub-path the action picks up Jellyfin's *Base URL*, so
`/jellyfin/sso/oidc/start/<providerId>` works.

**Inside the login form (built in):** switch on *Put the button inside the login form* on
the **Sessions** tab. The plugin then adds one `<script>` tag to Jellyfin's page as it is
served — nothing is written to disk, nothing in the web folder changes, and turning the
switch off removes it again. The button lands next to *Sign In*, with the provider's mark
on it.

Turn the branding button off at the same time unless you want one in both places.

**JavaScript Injector**, if you already run that community plugin and prefer to keep your
own snippet, works too:

```js
// Puts an SSO button under Jellyfin's login form.
document.addEventListener('DOMContentLoaded', function () {
  const form = document.querySelector('.loginForm, form.login');
  if (!form || document.getElementById('ssoButton')) return;
  const button = document.createElement('a');
  button.id = 'ssoButton';
  button.className = 'raised block emby-button';
  button.href = '/sso/oidc/start';   // asks which provider, if there are several
  button.textContent = 'Sign in with SSO';
  form.appendChild(button);
});
```

**The mark on the button** comes from the provider itself where possible: with *Use the
provider's own logo on the button* on (the default), the server fetches the logo your
provider shows on its own login page — Authentik's brand logo, otherwise the icon the
login page declares — caches it for twelve hours and serves it from
`/sso/icon/provider/<providerId>`. A household that rebranded its Authentik sees its own
mark in Jellyfin. The fetch happens on the server, so nobody's browser contacts the
provider before they choose to sign in, and anything that is not a small image is ignored.

Without a reachable logo, or with the setting off, the plugin's own marks are served:
`/sso/icon/authentik`, `/sso/icon/keycloak`, `/sso/icon/authelia`, `/sso/icon/google`,
`/sso/icon/okta`, `/sso/icon/auth0`, and `/sso/icon/generic` for anything else.

## Logout

Jellyfin's logout drops its own token and leaves the provider session alone, so the next
login goes through without credentials. With **Sign out of the identity provider too**
enabled, sending the browser to **`/sso/oidc/logout`** after Jellyfin's logout ends the
provider session as well and returns to the web client.

With several providers, `/sso/oidc/logout` uses the first enabled one;
`/sso/oidc/logout/<providerId>` names it.

**The switch alone changes nothing visible** — it only decides what `/sso/oidc/logout`
does. A server plugin cannot touch Jellyfin's logout button, so wiring the two together
needs the **JavaScript Injector** plugin:

```js
// Sends the browser on to the provider's logout once Jellyfin has signed the user out.
document.addEventListener('click', function (event) {
  if (event.target.closest('[data-action="logout"], .btnLogout')) {
    setTimeout(function () { window.location.href = '/sso/oidc/logout'; }, 500);
  }
});
```

If the provider advertises no `end_session_endpoint`, or cannot be reached, the user still
lands back in Jellyfin — a broken provider must never block a logout.
