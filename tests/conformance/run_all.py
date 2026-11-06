#!/usr/bin/env python3
"""Run every module of the plan and record both verdicts: the suite's, and whether the
plugin actually completed the login.

The second one is the evidence that matters for the negative modules. The suite judges
what it can see from the OP side; whether the relying party went on to sign somebody in
is visible only at the relying party, and that is the question those modules ask.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from drive import RUNS  # noqa: E402
from run_module import run  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# Modules where the plugin is expected to refuse the login. Everything else is a login
# that should succeed, or a facility the plugin does not claim to have.
MUST_REFUSE = {
    "oidcc-client-test-nonce-invalid",
    "oidcc-client-test-missing-iat",
    "oidcc-client-test-missing-aud",
    "oidcc-client-test-invalid-aud",
    "oidcc-client-test-idtoken-sig-none",
    "oidcc-client-test-missing-sub",
    "oidcc-client-test-invalid-sig-rs256",
    "oidcc-client-test-invalid-iss",
    "oidcc-client-test-userinfo-invalid-sub",
    "oidcc-client-test-invalid-sig-es256",
    "oidcc-client-test-discovery-issuer-mismatch",
}

# Modules that ask for something this plugin does not implement and does not claim to.
NOT_CLAIMED = {
    "oidcc-client-test-discovery-webfinger-acct",
    "oidcc-client-test-discovery-webfinger-url",
    "oidcc-client-test-client-secret-basic",
}


def completed(out):
    """Did the plugin sign somebody in?"""
    body = out.get("rpBody") or ""
    return out.get("rpHttpStatus") == 200 and "Completing login" in body


def main():
    plan = json.load(open(os.path.join(RUNS, "plan.json")))
    results = []
    for i, module in enumerate(plan["modules"], 1):
        print(f"[{i}/{len(plan['modules'])}] {module}", flush=True)
        try:
            out = run(module, plan["planId"])
        except Exception as e:  # noqa: BLE001
            out = {"module": module, "error": f"{type(e).__name__}: {e}"}
        out["pluginCompletedLogin"] = completed(out) if "error" not in out else None
        if module in MUST_REFUSE:
            out["expected"] = "refuse"
            out["verdict"] = "OK" if out["pluginCompletedLogin"] is False else "LOOK"
        elif module in NOT_CLAIMED:
            out["expected"] = "not claimed"
            out["verdict"] = "n/a"
        else:
            out["expected"] = "accept"
            out["verdict"] = "OK" if out["pluginCompletedLogin"] else "LOOK"
        print(
            f"      suite={out.get('result')} plugin_login={out.get('pluginCompletedLogin')} "
            f"-> {out['verdict']}",
            flush=True,
        )
        results.append(out)
        with open(os.path.join(RUNS, "results.json"), "w") as f:
            json.dump(results, f, indent=2)
        time.sleep(1)

    print("\n=== summary ===")
    for r in results:
        print(f"{r['verdict']:5} {r['module']:60} suite={r.get('result')}")


if __name__ == "__main__":
    main()
