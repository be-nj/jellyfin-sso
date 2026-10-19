# Login button and logout

Back to the [documentation index](README.md) · [README](../README.md)

## Login button

**Branding (built in):** the plugin adds one button per enabled provider to Jellyfin's
*Login Disclaimer* whenever the configuration is saved. The buttons sit between
`<!-- sso-login-buttons:start -->` and `:end`, and only that part is ever rewritten —
anything else you put in the disclaimer stays. Disabling every provider removes the block
and leaves your text alone.

If Jellyfin is served under a sub-path, the links pick it up from the server's network
configuration, so `/jellyfin/sso/oidc/start/<providerId>` works as well.

**JavaScript Injector (a nicer button):** if you run that community plugin, this adds a
button into the login form itself rather than below it:

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

Jellyfin's own logout drops its token but leaves the identity provider session alone, so
the next login goes through without asking for credentials. With **Sign out of the
identity provider too** enabled, sending the browser to **`/sso/oidc/logout`** after
Jellyfin's logout ends the provider session as well and comes back to the web client.

**On its own, the switch changes nothing you can see.** It only decides what
`/sso/oidc/logout` does when something sends the browser there. A server plugin cannot
change Jellyfin's own logout button, so the last step needs a snippet in the browser,
which means the **JavaScript Injector** plugin — a separate community plugin, installed
from its own repository the same way this one was. With it:

```js
// Sends the browser on to the provider's logout once Jellyfin has signed the user out.
document.addEventListener('click', function (event) {
  if (event.target.closest('[data-action="logout"], .btnLogout')) {
    setTimeout(function () { window.location.href = '/sso/oidc/logout'; }, 500);
  }
});
```

Without the injector you can still visit `/sso/oidc/logout` yourself; the switch alone
changes nothing about Jellyfin's button.

If the provider advertises no `end_session_endpoint`, or cannot be reached, the user still
lands back in Jellyfin — a broken provider must never block a logout.
