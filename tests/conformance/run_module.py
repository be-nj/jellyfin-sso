#!/usr/bin/env python3
"""Run one conformance module against the plugin and report what the suite concluded."""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from drive import (  # noqa: E402
    ALIAS, RUNS, SUITE, PROVIDER_ID, configure_provider, drive_login, jellyfin_token, stack, suite,
)

HERE = os.path.dirname(os.path.abspath(__file__))


def run(module, plan_id, variant_json=None, verbose=False):
    s = stack()
    base = s["jellyfin"]
    token = jellyfin_token(base)
    issuer = f"{SUITE}/test/a/{ALIAS}/"

    params = {"test": module, "plan": plan_id}
    if variant_json:
        params["variant"] = variant_json
    created = suite("POST", "/api/runner", params)
    test_id = created["id"]

    # The test is now serving its OP at the plan's alias and waiting for a login.
    for _ in range(30):
        state = suite("GET", f"/api/runner/{test_id}")
        if state.get("status") in ("WAITING", "RUNNING", "FINISHED", "INTERRUPTED"):
            break
        time.sleep(1)

    configure_provider(base, token, issuer)
    final_url, status, body, _ = drive_login(base)

    # Give the suite a moment to finish judging, then let it stop waiting.
    for _ in range(40):
        state = suite("GET", f"/api/runner/{test_id}")
        if state.get("status") == "FINISHED":
            break
        time.sleep(1)
    else:
        try:
            suite("POST", f"/api/runner/{test_id}", {"id": test_id})
        except Exception:  # noqa: BLE001
            pass
        time.sleep(2)

    info = suite("GET", f"/api/info/{test_id}")
    log = suite("GET", f"/api/log/{test_id}") or []
    return {
        "module": module,
        "testId": test_id,
        "status": info.get("status"),
        "result": info.get("result"),
        "rpFinalUrl": final_url,
        "rpHttpStatus": status,
        "rpBody": body[:600] if body else None,
        "failures": [
            {"src": e.get("src"), "msg": e.get("msg"), "result": e.get("result")}
            for e in log
            if e.get("result") in ("FAILURE", "WARNING")
        ],
    }


if __name__ == "__main__":
    plan = json.load(open(os.path.join(RUNS, "plan.json")))
    module = sys.argv[1]
    out = run(module, plan["planId"])
    print(json.dumps(out, indent=2)[:4000])
