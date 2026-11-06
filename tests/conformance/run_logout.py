#!/usr/bin/env python3
"""Run the suite's logout plans against the plugin.

Back-channel logout is the interesting half: the suite posts a logout token straight to
the plugin's endpoint, including the ones the specification says must be refused — a
token signed with "none", with the wrong algorithm, audience or issuer, with no events
claim, with the wrong event, and with a nonce. Those are exactly the refusals
LogoutTokenValidator exists for, and until now nothing outside this repository had
tried them.
"""
import json
import os
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import http.cookiejar

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from drive import (  # noqa: E402
    CA, RUNS, CLIENT_ID, PROVIDER_ID, SUITE, jellyfin, jellyfin_token, stack, suite,
)

HERE = os.path.dirname(os.path.abspath(__file__))
ALIAS = "jellyfin-sso-logout"
ctx = ssl.create_default_context(cafile=CA)

# What the plugin must refuse. Each name says which rule is under test.
MUST_REFUSE = {
    "oidcc-client-test-rp-backchannel-rpinitlogout-alg-none": "an unsigned logout token",
    "oidcc-client-test-rp-backchannel-rpinitlogout-wrong-alg": "a token signed with a symmetric key",
    "oidcc-client-test-rp-backchannel-rpinitlogout-wrong-aud": "a token for another audience",
    "oidcc-client-test-rp-backchannel-rpinitlogout-wrong-iss": "a token from another issuer",
    "oidcc-client-test-rp-backchannel-rpinitlogout-no-event": "a token with no events claim",
    "oidcc-client-test-rp-backchannel-rpinitlogout-wrong-event": "a token naming another event",
    "oidcc-client-test-rp-backchannel-rpinitlogout-with-nonce": "a token carrying a nonce",
}


def configure(base, token, issuer):
    config = jellyfin(base, token, "GET", "/sso/admin/config")
    config["Providers"] = [
        {
            "Id": PROVIDER_ID,
            "Name": "Conformance suite",
            "Enabled": True,
            "Authority": issuer,
            "ClientId": CLIENT_ID,
            "Scopes": ["openid", "profile", "email"],
            "GroupsClaim": "groups",
            "GroupsFromUserInfo": False,
            "AcceptBackChannelLogout": True,
        }
    ]
    config["SignOutOfIdentityProvider"] = True
    config["LoginRequestsPerMinute"] = 0
    config["Links"] = []
    jellyfin(base, token, "POST", "/sso/admin/config", config)


def login_then_logout(base):
    """A full login, then the sign-out navigation, in one browser's worth of cookies."""
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(
        urllib.request.HTTPCookieProcessor(jar),
        urllib.request.HTTPSHandler(context=ctx),
    )
    steps = []
    for label, url in (
        ("login", f"{base}/sso/oidc/start/{PROVIDER_ID}"),
        ("logout", f"{base}/sso/oidc/logout/{PROVIDER_ID}"),
    ):
        try:
            with opener.open(url, timeout=60) as r:
                body = r.read(2000).decode("utf-8", "replace")
                steps.append({"step": label, "status": r.status, "url": r.geturl(),
                               "completed": "Completing login" in body})
        except urllib.error.HTTPError as e:
            steps.append({"step": label, "status": e.code, "url": e.geturl(),
                           "body": e.read(400).decode("utf-8", "replace")})
        except Exception as e:  # noqa: BLE001
            steps.append({"step": label, "error": f"{type(e).__name__}: {e}"})
    return steps


def main():
    s = stack()
    base = s["jellyfin"]
    token = jellyfin_token(base)
    issuer = f"{SUITE}/test/a/{ALIAS}/"
    variant = {
        "client_registration": "static_client",
        "client_auth_type": "none",
        "request_type": "plain_http_request",
        "response_mode": "default",
    }
    # The two addresses are reached by different parties, so they are spelled
    # differently. The redirect and the post-logout redirect are followed by the browser,
    # which here is this script on the Docker host, so "localhost" is right for them. The
    # back-channel logout URI is called by the suite's own server container, where
    # "localhost" is that container — the first run failed all eight modules identically
    # with "RestClientException happened whilst calling logout endpoint" for exactly that
    # reason. From inside the suite, the Docker host is host.docker.internal, which the
    # compose override gives it as an extra host.
    port = base.rsplit(":", 1)[1]
    from_the_suite = f"http://host.docker.internal:{port}"
    config = {
        "alias": ALIAS,
        "description": "jellyfin-sso: back-channel logout",
        "client": {
            "client_id": CLIENT_ID,
            "redirect_uri": f"{base}/sso/oidc/callback/{PROVIDER_ID}",
            "post_logout_redirect_uri": f"{base}/web/index.html",
            "backchannel_logout_uri":
                f"{from_the_suite}/sso/oidc/backchannel-logout/{PROVIDER_ID}",
        },
    }
    plan = suite("POST", "/api/plan",
                 {"planName": "oidcc-client-back-channel-logout-rp-basic",
                  "variant": json.dumps(variant)}, config)
    plan_id = plan["id"]
    modules = [m["testModule"] for m in plan["modules"]]
    print(f"plan {plan_id}  issuer {issuer}")
    print(f"backchannel_logout_uri {config['client']['backchannel_logout_uri']}")

    results = []
    for i, module in enumerate(modules, 1):
        print(f"[{i}/{len(modules)}] {module}", flush=True)
        created = suite("POST", "/api/runner", {"test": module, "plan": plan_id})
        test_id = created["id"]
        time.sleep(2)
        configure(base, token, issuer)
        steps = login_then_logout(base)
        for _ in range(45):
            state = suite("GET", f"/api/runner/{test_id}")
            if state.get("status") == "FINISHED":
                break
            time.sleep(2)
        info = suite("GET", f"/api/info/{test_id}")
        log = suite("GET", f"/api/log/{test_id}") or []
        out = {
            "module": module,
            "testId": test_id,
            "expects": MUST_REFUSE.get(module, "the logout to be accepted"),
            "mustRefuse": module in MUST_REFUSE,
            "status": info.get("status"),
            "result": info.get("result"),
            "steps": steps,
            "notes": [
                {"src": e.get("src"), "msg": e.get("msg"), "result": e.get("result")}
                for e in log if e.get("result") in ("FAILURE", "WARNING", "REVIEW")
            ][:12],
        }
        print(f"      suite={out['result']}  {len(out['notes'])} note(s)", flush=True)
        results.append(out)
        with open(os.path.join(RUNS, "results-logout.json"), "w") as f:
            json.dump(results, f, indent=2)
    print("\n=== summary ===")
    for r in results:
        print(f"{r.get('result'):10} {r['module']}")


if __name__ == "__main__":
    main()
