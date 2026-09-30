"""Runs the agent tests attached to the operations agent (tests/*.json, attached by deploy.py) and prints the result.

Exit code 1 if any test fails, so it can gate a change to the prompt or the guardrails. Together with e2e.mjs (the real
tools, end to end) it shows that the guardrails block what they should and nothing more.

    ELEVENLABS_API_KEY=... python agent/run_tests.py [--verbose]
"""
from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

API = "https://api.elevenlabs.io/v1/convai"
HERE = Path(__file__).resolve().parent


def api(path: str, method: str = "GET", body: dict | None = None) -> dict:
    req = urllib.request.Request(API + path, data=json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"xi-api-key": os.environ["ELEVENLABS_API_KEY"], "content-type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read().decode() or "{}")
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {path} -> {e.code} {e.read().decode()[:500]}") from e


def main() -> int:
    state = json.loads((HERE / "agent.json").read_text(encoding="utf-8"))
    tests = list(state.get("tests", {}).values())
    if not tests:
        print("No tests: run deploy.py first.")
        return 1
    invocation = api(f"/agents/{state['agent_id']}/run-tests", "POST", {"tests": [{"test_id": t} for t in tests]})
    runs: list[dict] = []
    started = time.time()
    while time.time() - started < 600:
        time.sleep(5)
        runs = api(f"/test-invocations/{invocation['id']}").get("test_runs", []) or []
        if runs and all(r.get("status") in ("passed", "failed", "error") for r in runs):
            break
    failures = 0
    for r in runs:
        ok = r.get("status") == "passed"
        failures += 0 if ok else 1
        rationale = ((r.get("condition_result") or {}).get("rationale") or {}).get("summary") or ""
        print(f"{'ok  ' if ok else 'FAIL'} {r.get('test_name') or r.get('test_id')}" + ("" if ok else f"  – {rationale[:160]}"))
        if not ok or "--verbose" in sys.argv:
            for message in r.get("agent_responses", []) or []:
                print(f"       [{message.get('role')}] {(message.get('message') or '')[:300]}")
    print(f"{len(runs) - failures}/{len(runs)} passed")
    return 1 if failures or not runs else 0


if __name__ == "__main__":
    sys.exit(main())
