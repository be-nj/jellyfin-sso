#!/usr/bin/env python3
"""Run every module of the plan and record two verdicts: the suite's, and who stopped
the flow.

The second one is the evidence that matters for the negative modules. The suite judges
what it can see from the OP side; whether the relying party refused is visible only at
the relying party, and that is the question those modules ask.

"Who stopped it" rather than "did the login complete", because those are not the same
claim and the weaker one hands out false passes: a flow the *suite* ended at its own
authorization endpoint also failed to complete, and reading that as a refusal by the
plugin is how discovery-issuer-mismatch looked green in the first run.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from drive import RUNS, who_stopped  # noqa: E402
from run_module import run  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

# Modules where the plugin is expected to refuse the login. Everything else is a login
# that should succeed, or a module outside this plan's claimed coverage.
#
# discovery-issuer-mismatch belongs here but cannot pass here: every module in a plan
# shares one issuer, so by the time it runs the plugin has the discovery document cached
# and never fetches the broken one. It reads LOOK with the suite named as the party that
# stopped the flow, which is the honest answer — run_discovery.py gives it a cold cache
# and its own provider id, and it passes there.
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

# Modules the plugin refuses on purpose, each written up in the review: userinfo as a
# form parameter in the request body, which RFC 6750 SHOULD NOTs, and a signed userinfo
# response, which a client gets only if it registered userinfo_signed_response_alg. The
# plugin refuses rather than reading an unverified payload. The refusal has to be the
# plugin's own, so these are checked exactly like MUST_REFUSE and only labelled apart —
# a suite verdict of FAILED on them is the expected outcome, not a finding.
REFUSES_BY_DESIGN = {
    "oidcc-client-test-userinfo-bearer-body",
    "oidcc-client-test-userinfo-signed",
}

# WebFinger and fetching distributed claims are not implemented. client-secret-basic
# needs a confidential-client plan; this run configures a public client, though the
# plugin also supports client secrets. A completed login alone does not prove support
# for distributed claims: that module requires a request to a separate claims endpoint.
NOT_CLAIMED = {
    "oidcc-client-test-discovery-webfinger-acct",
    "oidcc-client-test-discovery-webfinger-url",
    "oidcc-client-test-client-secret-basic",
    "oidcc-client-test-distributed-claims",
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
        out["stoppedBy"] = who_stopped(out.get("rpFinalUrl"), out.get("rpBody"))
        if module in MUST_REFUSE or module in REFUSES_BY_DESIGN:
            out["expected"] = "refuse by design" if module in REFUSES_BY_DESIGN else "refuse"
            out["verdict"] = "OK" if out["stoppedBy"] == "the plugin" else "LOOK"
        elif module in NOT_CLAIMED:
            out["expected"] = "not claimed"
            out["verdict"] = "n/a"
        else:
            out["expected"] = "accept"
            out["verdict"] = "OK" if out["stoppedBy"] == "nobody" else "LOOK"
        print(
            f"      suite={out.get('result')} stopped_by={out['stoppedBy']} "
            f"-> {out['verdict']}",
            flush=True,
        )
        results.append(out)
        with open(os.path.join(RUNS, "results.json"), "w") as f:
            json.dump(results, f, indent=2)
        time.sleep(1)

    print("\n=== summary ===")
    for r in results:
        print(f"{r['verdict']:5} {r['module']:60} "
              f"suite={str(r.get('result')):8} stopped_by={r.get('stoppedBy')}")


if __name__ == "__main__":
    main()
