#!/usr/bin/env python3
"""Drive the OpenID Foundation conformance suite's RP tests against the jellyfin-sso
plugin running in the manual e2e stack.

The suite acts as the identity provider. Each test module serves a (possibly broken)
OP at one stable issuer URL, waits for the relying party to start a login, answers it,
and then judges what the RP did.
"""
import json
import os
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import http.cookiejar

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

SUITE = "https://localhost.emobix.co.uk:8443"

# The certificate the suite is given to serve, which is the same one the end-to-end
# stack mints and mounts into the Jellyfin container as a trusted CA. The suite's own
# image ships one for "CN=localhost" with no subject alternative name, which no current
# TLS stack accepts for any hostname, so it has to be replaced — see setup.sh.
CA = os.path.join(REPO, "tests", "runs", "e2e", "certs", "idp.crt")

# Everything a run produces goes here, like every other test output in this repository.
RUNS = os.path.join(REPO, "tests", "runs", "conformance")

PROVIDER_ID = "conformance"
CLIENT_ID = "jellyfin-sso-conformance"
ALIAS = "jellyfin-sso"

os.makedirs(RUNS, exist_ok=True)

ctx = ssl.create_default_context(cafile=CA)


def suite(method, path, params=None, body=None):
    url = SUITE + path
    if params:
        url += "?" + urllib.parse.urlencode(params)
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, context=ctx, timeout=60) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {path} -> {e.code}: {e.read()[:400].decode()}") from None


def stack():
    """Where the server is now.

    Not read from stack.json: Testcontainers publishes the server on an ephemeral host
    port, and Docker hands out a different one every time the container is restarted, so
    the file goes stale the moment anything restarts. The container is the only thing that
    knows, so ask it.
    """
    name = subprocess.run(
        ["docker", "ps", "--filter", "ancestor=jellyfin/jellyfin:12.0", "--format", "{{.Names}}"],
        capture_output=True, text=True, check=True).stdout.split()
    if not name:
        raise RuntimeError("no running jellyfin container; is the manual stack up?")
    port = subprocess.run(
        ["docker", "port", name[0], "8096/tcp"],
        capture_output=True, text=True, check=True).stdout.splitlines()[0].rsplit(":", 1)[1]
    return {"jellyfin": f"http://localhost:{port}", "container": name[0]}


def jellyfin_token(base):
    body = json.dumps({"Username": "e2e-admin", "Pw": "e2e-admin-password"}).encode()
    req = urllib.request.Request(base + "/Users/AuthenticateByName", data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header(
        "Authorization",
        'MediaBrowser Client="conformance", Device="driver", DeviceId="conformance-driver", Version="1.0.0"',
    )
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read())["AccessToken"]


def jellyfin(base, token, method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method)
    req.add_header("Authorization", f'MediaBrowser Token="{token}"')
    if data:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=30) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def configure_provider(
    base, token, issuer, provider_id=PROVIDER_ID, algorithms=()
):
    """Point the plugin at the suite's OP, as an operator would.

    No signing algorithm is pinned, which is the plugin's default shape and what an
    operator who never touches the field gets. That is deliberate: the login plan has a
    module that signs a token with ES256 and a broken signature, and pinning RS256 would
    make the plugin refuse it for the algorithm before the signature is ever checked —
    the module would pass without testing what it is for.

    The one plan that does need a pin is the back-channel logout plan, whose
    rpinitlogout-wrong-alg module signs with ES256 using a second key out of the same
    published set: a client that accepts every asymmetric algorithm cannot tell that
    apart from a legitimate token. run_logout.py configures RS256 itself for that reason.
    """
    config = jellyfin(base, token, "GET", "/sso/admin/config")
    config["Providers"] = [
        {
            "Id": provider_id,
            "Name": "Conformance suite",
            "Enabled": True,
            "Authority": issuer,
            "ClientId": CLIENT_ID,
            "Scopes": ["openid", "profile", "email"],
            "GroupsClaim": "groups",
            "GroupsFromUserInfo": False,
            "AcceptBackChannelLogout": True,
            "SigningAlgorithms": list(algorithms),
        }
    ]
    config["LoginRequestsPerMinute"] = 0
    config["Links"] = []
    jellyfin(base, token, "POST", "/sso/admin/config", config)


def who_stopped(final_url, body):
    """Who ended the flow: the plugin, the suite, or nobody.

    This is the question every negative module actually asks, and "the login did not
    complete" does not answer it. A flow that ends on the suite's own address was stopped
    by the *suite* — for a module that requires the client to refuse, that is a finding
    dressed as a pass. Taken from the host of the last address the login reached.
    """
    if final_url is None:
        return "unclear"
    if "Completing login" in (body or ""):
        return "nobody"
    host = urllib.parse.urlparse(final_url).netloc
    return "the suite" if host.startswith("localhost.emobix.co.uk") else "the plugin"


def drive_login(base, provider_id=PROVIDER_ID, verbose=False):
    """Follow the plugin's login from the start endpoint to wherever it ends.

    Returns (final_url, status, body_excerpt, hops).
    """
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(
        urllib.request.HTTPCookieProcessor(jar),
        urllib.request.HTTPSHandler(context=ctx),
    )
    url = f"{base}/sso/oidc/start/{provider_id}"
    hops = []
    try:
        with opener.open(url, timeout=60) as r:
            body = r.read(4000).decode("utf-8", "replace")
            hops.append(r.geturl())
            return r.geturl(), r.status, body, hops
    except urllib.error.HTTPError as e:
        return e.geturl(), e.code, e.read(4000).decode("utf-8", "replace"), hops
    except Exception as e:  # noqa: BLE001
        return None, None, f"{type(e).__name__}: {e}", hops


def main():
    s = stack()
    base = s["jellyfin"]
    token = jellyfin_token(base)
    redirect_uri = f"{base}/sso/oidc/callback/{PROVIDER_ID}"
    print(f"jellyfin      {base}")
    print(f"redirect_uri  {redirect_uri}")

    variant = {
        "client_registration": "static_client",
        "response_type": "code",
        "client_auth_type": "none",
        "request_type": "plain_http_request",
        "response_mode": "default",
    }
    config = {
        "alias": ALIAS,
        "description": "jellyfin-sso: PKCE public client, authorization code flow",
        "client": {"client_id": CLIENT_ID, "redirect_uri": redirect_uri},
    }
    plan = suite(
        "POST",
        "/api/plan",
        {"planName": "oidcc-client-test-plan", "variant": json.dumps(variant)},
        config,
    )
    plan_id = plan["id"]
    modules = [m["testModule"] for m in plan["modules"]]
    print(f"plan          {plan_id}  ({len(modules)} modules)")
    print(f"issuer        {SUITE}/test/a/{ALIAS}/")
    with open(os.path.join(RUNS, "plan.json"), "w") as f:
        json.dump({"planId": plan_id, "modules": modules, "redirectUri": redirect_uri}, f, indent=2)
    for m in modules:
        print("   -", m)


if __name__ == "__main__":
    main()
