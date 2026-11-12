#!/usr/bin/env python3
"""Re-run the discovery modules against a plugin that has to fetch the document.

The plugin caches a provider's discovery document for thirty minutes under the key
"{provider id}|{authority}", so in the first pass three modules never saw a fetch at
all: the plugin went straight to the authorization endpoint with endpoints it already
had, and the suite refused there. That verdict says nothing about the plugin.

Restarting the server was the first idea and it was wrong: Testcontainers publishes the
server on an ephemeral host port and Docker hands out a new one on restart, which
invalidates the redirect URI the suite has registered. Changing the *provider id*
instead moves the cache key without touching anything else, so each module gets its own
provider, its own plan, and a document the plugin has never seen.

It also records who stopped the flow. "The login did not complete" is not the same claim
as "the plugin refused it": if the last address is on the suite's host, the suite stopped
it, and that is a finding rather than a pass.
"""
import json
import os
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from drive import (  # noqa: E402
    CLIENT_ID, RUNS, SUITE, configure_provider, drive_login, jellyfin_token, stack, suite,
    who_stopped,
)

HERE = os.path.dirname(os.path.abspath(__file__))
MODULES = [
    "oidcc-client-test-discovery-openid-config",
    "oidcc-client-test-discovery-jwks-uri-keys",
    "oidcc-client-test-discovery-issuer-mismatch",
]
VARIANT = {
    "client_registration": "static_client",
    "response_type": "code",
    "client_auth_type": "none",
    "request_type": "plain_http_request",
    "response_mode": "default",
}


def ran_to_completion(log):
    """Record completion and earlier failures alongside the suite's final verdict."""
    before, finished = [], False
    for e in log:
        if e.get("result") == "FINISHED":
            finished = True
            break
        if e.get("result") == "FAILURE":
            before.append(str(e.get("msg"))[:200])
    return finished, before


def main():
    base = stack()["jellyfin"]
    token = jellyfin_token(base)
    results = []
    # A second invocation must also miss the plugin's thirty-minute discovery cache.
    run_id = uuid.uuid4().hex[:12]
    for i, module in enumerate(MODULES, 1):
        provider_id = f"cold{i}-{run_id}"
        alias = f"jellyfin-sso-{provider_id}"
        issuer = f"{SUITE}/test/a/{alias}/"
        redirect_uri = f"{base}/sso/oidc/callback/{provider_id}"
        print(f"[{i}/{len(MODULES)}] {module}  (provider {provider_id})", flush=True)

        plan = suite("POST", "/api/plan",
                     {"planName": "oidcc-client-test-plan", "variant": json.dumps(VARIANT)},
                     {"alias": alias,
                      "description": f"jellyfin-sso cold discovery: {module}",
                      "client": {"client_id": CLIENT_ID, "redirect_uri": redirect_uri}})
        created = suite("POST", "/api/runner", {"test": module, "plan": plan["id"]})
        test_id = created["id"]
        time.sleep(2)

        configure_provider(base, token, issuer, provider_id)
        # The positive modules finish after discovery/JWKS. Following the redirect
        # would send authorization to an already finished test and turn it FAILED.
        final_url, status, body, _ = drive_login(base, provider_id, follow_redirects=False)

        for _ in range(40):
            state = suite("GET", f"/api/info/{test_id}") or {}
            if state.get("status") in ("FINISHED", "INTERRUPTED"):
                break
            time.sleep(2)
        info = suite("GET", f"/api/info/{test_id}")
        log = suite("GET", f"/api/log/{test_id}") or []
        finished, failed_before = ran_to_completion(log)
        out = {
            "module": module,
            "testId": test_id,
            "providerId": provider_id,
            "suiteResult": info.get("result"),
            "stoppedBy": "the driver" if status == 302 else who_stopped(final_url, body),
            "ranToCompletion": finished,
            "failuresBeforeCompletion": failed_before,
            "rpHttpStatus": status,
            "rpFinalUrl": final_url,
            "rpBody": (body or "")[:400],
            "notes": [
                {"src": e.get("src"), "msg": e.get("msg"), "result": e.get("result")}
                for e in log if e.get("result") in ("FAILURE", "WARNING", "REVIEW")
            ][:10],
        }
        expected_status = 400 if module.endswith("issuer-mismatch") else 302
        out["verdict"] = (
            "OK" if out["suiteResult"] == "PASSED" and finished
            and not failed_before and status == expected_status else "LOOK"
        )
        print(f"      suite={out['suiteResult']}  stopped_by={out['stoppedBy']}  "
              f"rp={status}  ran_to_completion={finished}"
              f"{'  (failed before that)' if failed_before else ''}", flush=True)
        for n in out["notes"][:3]:
            print(f"        {n['result']}: {str(n['msg'])[:140]}", flush=True)
        results.append(out)
        with open(os.path.join(RUNS, "results-discovery.json"), "w") as f:
            json.dump(results, f, indent=2)

    print("\n=== summary ===")
    for r in results:
        print(f"{r['module']:58} suite={str(r['suiteResult']):8} "
              f"stopped_by={r['stoppedBy']:10} "
              f"ran_to_completion={r['ranToCompletion']} verdict={r['verdict']}")
    if any(r["verdict"] != "OK" for r in results):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
