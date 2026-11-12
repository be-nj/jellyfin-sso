# Conformance tier

The OpenID Foundation conformance suite, run against this plugin as a relying party. The
suite acts as the identity provider: each test module serves an OpenID Provider that
answers correctly or in one specific broken way, waits for the plugin to start a login or
a logout, and then judges what the plugin did.

What it found the first time it was run is in
[docs/reviews/2026-11-06-oidc-conformance.md](../../docs/reviews/2026-11-06-oidc-conformance.md).
The [rerun in November's code and security review](../../docs/reviews/2026-11-10-code-security-review.md#oidc-conformance-rerun)
records the latest run and the driver corrections described below.

Run by hand, not in CI. It needs two container stacks; allow about an hour for all plans.
The separate real-provider tier for Authentik, Keycloak and Authelia runs in the Tests
workflow on code changes; it is not the same check as this conformance tier.

## What it needs

- Docker with the Compose plugin, and about 4 GB free for the suite's three containers.
- `localhost.emobix.co.uk` resolving to `127.0.0.1`. It does so in public DNS, so there is
  usually nothing to do; `getent hosts localhost.emobix.co.uk` says whether it works here.
- The manual end-to-end stack, for a real Jellyfin with the plugin installed.

## Running it

In one terminal, a server to test:

```shell
E2E_STACK_MINUTES=90 make stack
```

In another, the suite — this hands it the certificate the stack just minted, which is the
one the Jellyfin container trusts:

```shell
tests/conformance/setup.sh
```

Then the plans, from the repository root:

```shell
python3 -I tests/conformance/drive.py          # create the plan, print the issuer
python3 -I tests/conformance/run_all.py        # the 28 login modules
python3 -I tests/conformance/run_discovery.py  # the 3 discovery modules, cold
python3 -I tests/conformance/run_logout.py     # the 8 back-channel logout modules
```

Everything a run produces lands in `tests/runs/conformance/`, which is gitignored:
`plan.json`, `results.json`, `results-discovery.json`, `results-logout.json`, and the
certificate and compose files the suite is started with. The suite's own web interface at
<https://localhost.emobix.co.uk:8443/> shows every request and response of every module,
which is where to look when a verdict needs explaining.

Afterwards:

```shell
cd tests/runs/conformance
docker compose -f docker-compose-prebuilt.yml -f docker-compose.override.yml down
```

## Reading a result

The login and discovery scripts record both the suite verdict and the observed flow:

- **`result` / `suiteResult`** — the suite's own verdict, from the provider's side.
- **`stoppedBy`** — taken from the host of the last address the login reached, by the
  `who_stopped` in `drive.py` that every script shares. A login that ended on the plugin's
  own address was refused by the plugin; a login that ended on the suite's address was
  stopped by the *suite*, which for a module that requires a refusal is a finding rather
  than a pass; a login that reached `Completing login` was stopped by nobody.

The logout script records the suite verdict under `result`, with browser requests in
`steps` and diagnostic messages in `notes`. A driver verdict of `OK` in the login plan
does not change a suite result of `FAILED`, `SKIPPED` or no result into `PASSED`.

The two are not the same claim, and conflating them produced three verdicts in the first
run that read as findings and were not. "The login did not complete" says nothing about
who decided that — and `run_all.py` went on using the weaker check for one more run, which
is how `discovery-issuer-mismatch` came back green a second time from a flow the suite had
ended. One shared helper, so there is one answer to the question.

`run_all.py` sorts the modules into four groups, and the verdict means something different
in each: a login that must complete, a refusal the plugin must make, a refusal the plugin
makes **by design** (`userinfo-bearer-body` and `userinfo-signed`, both written up in the
review), and modules outside the tested profile: unsupported WebFinger, fetching
distributed claims from a separate claims endpoint, and
`client_secret_basic`, which needs a confidential client instead of this public client.
The middle two
are checked identically — the refusal has to be the plugin's own — and only labelled apart.

The regular signing-key-rotation module needs **two logins**: its second authorization
request rotates the keys. `run_module.py` drives both and records `loginAttempts`;
one successful login alone leaves this module waiting and does not prove rotation.

The scripts poll `/api/info/{id}` for `status`; `/api/runner/{id}` does not include that
field. A timed-out module is stopped with `DELETE /api/runner/{id}`, not `POST` (which
starts or resumes it). The raw suite result is retained even when a module is interrupted.

`run_discovery.py` stops at the start endpoint's redirect after fetching discovery and
JWKS. It does not send an authorization request to a module that has already finished.
For these successful discovery checks, `stoppedBy` is **`the driver`**; this is not a
plugin refusal. The issuer-mismatch check must instead be refused with HTTP 400.
The script also records:

- **`ranToCompletion`** — did the module log `Test has run to completion`?
- **`failuresBeforeCompletion`** — which failures landed before it did?

Its verdict is `OK` only if the suite reports `PASSED`, the module completed without an
earlier failure, and the plugin returned the expected HTTP status. Otherwise the script
returns a nonzero exit status. Historical reports retain their original `FAILED` verdicts
from the earlier driver that continued into authorization after discovery was complete.

The server's own log is the third source and often the clearest:

```shell
docker logs $(docker ps -q --filter ancestor=jellyfin/jellyfin:12.0) 2>&1 \
  | grep -E "Rejected a token|Rejected a back-channel|refetching"
```

## Things the harness has to get right

Four of them, each learned by getting it wrong:

- **The suite's certificate has to be replaced.** Its nginx image ships one for
  `CN=localhost` with no subject alternative name at all, which no current TLS stack
  accepts for any hostname — and the plugin's discovery client refuses plain HTTP off
  loopback, so TLS cannot be avoided. `setup.sh` gives it the end-to-end stack's
  certificate, which `TestCertificate` puts the suite's hostname on.
- **The plugin caches discovery documents for thirty minutes**, keyed on
  `{provider id}|{authority}`, and every module in a plan shares one issuer. A module that
  is about the discovery document therefore needs a key the plugin has not seen:
  `run_discovery.py` gives each one its own provider id and its own plan, with a fresh
  suffix on every invocation so reruns also miss the cache. Restarting the
  server instead does not work — Testcontainers publishes it on an ephemeral port and
  Docker hands out a new one, which invalidates the redirect URI the suite has registered.
- **Two addresses are reached by two different parties.** The redirect URI is followed by
  the browser, so `localhost` is right for it. The back-channel logout URI is called by the
  suite's own container, where `localhost` is that container — it needs
  `host.docker.internal`, which the compose override adds as an extra host.
- **The suite must be able to reach the server at all.** `docker-compose.override.yml`
  gives its server container `host.docker.internal:host-gateway` for exactly that.

## Which plans

| Plan | Modules | What it is about |
|---|---|---|
| `oidcc-client-test-plan` | 28 | The login: ID token validation, userinfo, discovery, key rotation |
| `oidcc-client-back-channel-logout-rp-basic` | 8 | The logout token's refusals |

One module needs a provider setting rather than the default shape:
`oidcc-client-test-rp-backchannel-rpinitlogout-wrong-alg` signs the logout token with an
algorithm it did not register for, and a client that accepts any asymmetric algorithm
cannot tell. `run_logout.py` sets *Signing algorithms* to `RS256` itself, which is what the
suite registers, so there is nothing to fill in by hand.

Nothing else gets that pin, and that is deliberate: the login plan's `invalid-sig-es256`
signs a token with ES256 and a broken signature, and a plugin pinned to RS256 would refuse
it for the algorithm before the signature was ever checked — the module would pass without
testing what it is for. `configure_provider` therefore defaults to no pin at all, which is
also what an operator who never touches the field has.

Both are run with `client_registration: static_client`, `client_auth_type: none`,
`response_type: code` and `request_type: plain_http_request` — the plugin's default shape,
a PKCE public client on the authorization code flow.

The suite offers thirty more client plans. The certification plans
(`oidcc-client-basic-certification-test-plan` and friends) fix
`client_auth_type: client_secret_basic`, so running those means configuring a confidential
client and giving the secret to the container through `env:` or `file:`. The front-channel
logout and session-management plans cover features this plugin does not implement.
