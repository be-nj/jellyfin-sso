# Glossary

[Documentation index](README.md) · [README](../README.md)

| Word | In plain language |
|---|---|
| **OIDC / OpenID Connect** | The protocol behind "log in with …". OAuth2 is the older standard it builds on. |
| **Identity provider (IdP)** | The service that holds the accounts and checks passwords: your Authentik, Authelia or Keycloak. |
| **Claim** | A single fact the provider states about a person — their username, their email, their groups. |
| **Scope** | A bundle of claims the application asks for. `profile` usually carries the name, `email` the address. |
| **Issuer URL** | The provider's address for one application. Everything else is discovered from it; it has to answer at `<issuer>/.well-known/openid-configuration` **from inside the Jellyfin container**, not only in your browser. |
| **Subject (`sub`)** | The provider's id for a person. Often an unreadable string; with some settings it is the username. The plugin links accounts by it, so an account survives a rename **as long as the subject is not the username** — see [Linking accounts](linking.md#identity-matching-and-the-idp-subject). |
| **Client ID** | The name the provider knows your Jellyfin by. Not a secret. |
| **PKCE** | The mechanism that lets an application prove it started a login without holding a password. It is why no client secret is needed. |
| **Public client** | An application that holds no secret — anything running in a browser. The opposite is a confidential client. |
| **JIT** | "Just in time": the Jellyfin account is created at the first successful login rather than beforehand. The phrase shows up in the log line about a blocked login. |
| **Declarative** | The group mapping is the whole truth: on every login it is written over whatever was set before, including by hand. |
| **Redirect URI** | The address the provider sends the browser back to after the login. It must be registered there, character for character. |
| **Userinfo endpoint** | A second address at the provider that answers with the same facts about a person. Some providers keep the groups only there. |
| **targetAbi** | The Jellyfin version a plugin build was made for. Jellyfin compares it with its own version and hides builds that do not match, which is why the catalog sometimes shows nothing. |
