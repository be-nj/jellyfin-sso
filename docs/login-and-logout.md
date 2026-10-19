# Login button and logout

[Documentation index](README.md) · [README](../README.md)

## Login button

**Built in:** saving the configuration writes one button per enabled provider into
Jellyfin's *Login Disclaimer*. The buttons sit between `<!-- sso-login-buttons:start -->`
and `:end`; only that part is ever rewritten, so your own text stays, and disabling every
provider removes the block alone.

Under a sub-path the links pick up Jellyfin's *Base URL*, so
`/jellyfin/sso/oidc/start/<providerId>` works.

**JavaScript Injector**, if you run that community plugin, puts the button inside the
login form instead of below it:

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

## Logout

Jellyfin's logout drops its own token and leaves the provider session alone, so the next
login goes through without credentials. With **Sign out of the identity provider too**
enabled, sending the browser to **`/sso/oidc/logout`** after Jellyfin's logout ends the
provider session as well and returns to the web client.

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
