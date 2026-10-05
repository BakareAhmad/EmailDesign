#!/usr/bin/env bash
# ============================================================================
# Developer Portal — ONE clean final run -> evidence/ (repo root).
#   1. dotnet test (full suite, incl. 68 dev-portal tests) -> final-test-log.txt
#   2. App (in-memory DB, Paystack stub :9090, Brevo stub :9999):
#        /health (public, unchanged)                      -> final-health-log.txt
#        HTTP smoke: developer GETs every /dev page       -> final-dev-smoke-log.txt
#        direct-checkout regression journey (real payments:
#          success/decline/abandon/refund/custom)         -> final-checkout-regression-log.txt
#        dev-portal journey (screenshots 01..NN)          -> final-dev-portal-e2e-log.txt
#        fee CSV export (no PII)                          -> final-service-fee-export.csv
#   3. secret-leak sweep over every evidence file.
# Dry run: EV_DIR=/tmp/dp-dry SKIP_TESTS=1 bash e2e/dev-portal/run_final.sh
# Build first: dotnet build DeChimex.sln -c Debug.
# ============================================================================
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SLN="$ROOT/DeChimexPrints"
EV="${EV_DIR:-$ROOT/evidence}"
WIP=/home/user/e2e-dr-wip
TAG="${1:-devfinal}"
export DOTNET_ROOT=/var/tmp/dotnet PATH=/var/tmp/dotnet:$PATH NUGET_PACKAGES=/var/tmp/nuget \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PLAYWRIGHT_BROWSERS_PATH=/var/tmp/ms-playwright
PIDS=()
cleanup() {
  for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done
  [ -f /tmp/dp-paystack.pid ] && kill "$(cat /tmp/dp-paystack.pid)" 2>/dev/null; rm -f /tmp/dp-paystack.pid
  pkill -f "DeChimex[.]Host" 2>/dev/null; wait 2>/dev/null
}
trap cleanup EXIT
fail() { echo "FINAL RUN FAILED: $*"; exit 1; }
wait_port() { for _ in $(seq 1 90); do curl -s -o /dev/null "http://127.0.0.1:$1/" && return 0; sleep 1; done; return 1; }

# ---- clean slate ----
pkill -f "brevo_stub[.]py" 2>/dev/null; pkill -f "paystack_stub[.]py" 2>/dev/null; pkill -f "DeChimex[.]Host" 2>/dev/null; sleep 2
# The previous task's final evidence is preserved under its own harness, never mixed with this run.
if [ -z "${EV_DIR:-}" ] && [ -d "$ROOT/evidence" ] && [ ! -d "$ROOT/e2e/provider-keys/evidence-final-run" ] \
   && ls "$ROOT/evidence" | grep -q "checkout"; then
  mv "$ROOT/evidence" "$ROOT/e2e/provider-keys/evidence-final-run"
fi
rm -rf "$EV" "$WIP" /tmp/dp-helper-shots /tmp/dp-checkout-shots && mkdir -p "$EV" "$WIP"
python3 "$ROOT/e2e/design-review/fixtures.py" >/dev/null

if [ -z "${SKIP_TESTS:-}" ]; then
  echo "== 1. full test suite =="
  ( cd "$SLN" && dotnet test DeChimex.sln --no-build -c Debug ) > "$EV/final-test-log.txt" 2>&1 || { grep -E "Failed|\[FAIL\]" "$EV/final-test-log.txt" | head; fail "dotnet test"; }
  grep -E "Passed!|Failed!" "$EV/final-test-log.txt"
fi

echo "== 2. app with Paystack + Brevo stubs =="
python3 "$ROOT/e2e/design-review/brevo_stub.py" > /tmp/dp-brevo-stub.log 2>&1 & PIDS+=("$!")
python3 "$ROOT/e2e/payments/paystack_stub.py" > /tmp/dp-paystack-stub.log 2>&1 & PAYSTACK_PID=$!; PIDS+=("$PAYSTACK_PID")
sleep 1
( cd "$SLN" && env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:5119 \
    Database__UseInMemory=true Brevo__ApiBaseUrl=http://127.0.0.1:9999 Paystack__ApiBaseUrl=http://127.0.0.1:9090 \
    dotnet run --project src/DeChimex.Host --no-build --no-launch-profile -c Debug ) > /tmp/dp-app.log 2>&1 &
PIDS+=("$!"); wait_port 5119 || fail "app did not start"
sleep 3
curl -s http://127.0.0.1:5119/health | python3 -m json.tool > "$EV/final-health-log.txt"
python3 - "$EV/final-health-log.txt" <<'PY' || fail "public /health not Healthy"
import json,sys; h=json.load(open(sys.argv[1]))
print("public /health:", h["status"], {e["name"]: e["status"] for e in h["entries"]})
assert h["status"] == "Healthy"
PY
grep -q "Developer account" /tmp/dp-app.log && grep "Developer account" /tmp/dp-app.log | head -1 | sed 's/^/seed: /'

python3 "$ROOT/e2e/dev-portal/smoke.py" > "$EV/final-dev-smoke-log.txt" 2>&1 || { tail -20 "$EV/final-dev-smoke-log.txt"; fail "dev smoke"; }
tail -1 "$EV/final-dev-smoke-log.txt"

echo "== 3. checkout regression journey (generates real payments) =="
E2E_SHOTS_DIR=/tmp/dp-checkout-shots python3 "$ROOT/e2e/direct-checkout/workflow.py" "${TAG:0:6}dc" > "$EV/final-checkout-regression-log.txt" 2>&1 \
  || { tail -20 "$EV/final-checkout-regression-log.txt"; fail "checkout regression journey"; }
grep "RESULT" "$EV/final-checkout-regression-log.txt"
grep -q "0 failed" "$EV/final-checkout-regression-log.txt" || fail "checkout regression had failures"

echo "== 4. developer portal journey =="
E2E_SHOTS_DIR="$EV" PAYSTACK_STUB_PID="$PAYSTACK_PID" python3 "$ROOT/e2e/dev-portal/dev_portal_e2e.py" "${TAG:0:6}dp" \
  > "$EV/final-dev-portal-e2e-log.txt" 2>&1 || { grep -E "FAIL|Error|Traceback" "$EV/final-dev-portal-e2e-log.txt" | head -30; fail "dev portal journey"; }
tail -1 "$EV/final-dev-portal-e2e-log.txt"
grep -c "Unhandled exception\|fail: " /tmp/dp-app.log | sed 's/^/app log error lines: /'

echo "== 5. secret-leak sweep over evidence =="
python3 - "$SLN/src/DeChimex.Host/appsettings.json" "$EV" <<'PY' || fail "secret leaked into evidence"
import json,sys,pathlib
d=json.load(open(sys.argv[1])); secrets=[d["Brevo"]["ApiKey"], d["Paystack"]["SecretKey"]]
bad=[p.name for p in pathlib.Path(sys.argv[2]).iterdir() if p.is_file() and any(s.encode() in p.read_bytes() for s in secrets)]
print("leak sweep:", "clean" if not bad else bad); sys.exit(1 if bad else 0)
PY
echo "FINAL RUN PASSED — $(ls "$EV"/*.png | wc -l) screenshots in $EV"
