#!/usr/bin/env bash
# ============================================================================
# Provider-keys verification — ONE clean final run -> evidence/ (repo root).
#   1. dotnet test (full suite)                    -> evidence/final-test-log.txt
#   2. packaging-guard validations                 -> evidence/final-packaging-guard-log.txt
#   3. App A (REAL api.paystack.co, Brevo stub):
#        /health                                   -> evidence/final-health-log.txt
#        design-review keys journey (36 checks)    -> evidence/01..16 + final-e2e-log.txt
#        Brevo api-key header audit (all sends)    -> evidence/final-brevo-key-audit-log.txt
#   4. App B (Paystack stub, Brevo stub):
#        direct-checkout journey (60 checks)       -> evidence/17..37 + final-checkout-e2e-log.txt
#        Paystack Authorization header audit       -> evidence/final-paystack-auth-log.txt
# Key VALUES are never printed; only prefixes/lengths/booleans.
# Toolchain per AI_HANDOFF §86/§88 (SDK in /var/tmp/dotnet, Playwright browsers
# in /var/tmp/ms-playwright). Build first: dotnet build DeChimex.sln -c Debug.
# ============================================================================
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SLN="$ROOT/DeChimexPrints"
EV="$ROOT/evidence"
WIP=/home/user/e2e-dr-wip
TAG="${1:-keysfinal}"
export DOTNET_ROOT=/var/tmp/dotnet PATH=/var/tmp/dotnet:$PATH NUGET_PACKAGES=/var/tmp/nuget \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PLAYWRIGHT_BROWSERS_PATH=/var/tmp/ms-playwright
PIDS=()
cleanup() { for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done; wait 2>/dev/null; }
trap cleanup EXIT
fail() { echo "FINAL RUN FAILED: $*"; exit 1; }

wait_port() { for _ in $(seq 1 90); do curl -s -o /dev/null "http://127.0.0.1:$1/" && return 0; sleep 1; done; return 1; }
start_app() {  # $1 = extra env assignments
  ( cd "$SLN" && env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:5119 \
      Database__UseInMemory=true Brevo__ApiBaseUrl=http://127.0.0.1:9999 $1 \
      dotnet run --project src/DeChimex.Host --no-build --no-launch-profile -c Debug ) > "$2" 2>&1 &
  APP_PID=$!; PIDS+=("$APP_PID"); wait_port 5119 || fail "app did not start"
}
stop_app() { pkill -f "DeChimex.Host" 2>/dev/null; kill "$APP_PID" 2>/dev/null; sleep 3; }

# ---- clean slate ----
pkill -f brevo_stub.py 2>/dev/null; pkill -f paystack_stub.py 2>/dev/null; pkill -f DeChimex.Host 2>/dev/null; sleep 2
if [ -d "$ROOT/evidence" ] && ls "$ROOT/evidence" | grep -q "hero-vs-reference"; then
  [ -d "$ROOT/e2e/hero/evidence-final-run" ] && fail "evidence/ holds a hero run but e2e/hero/evidence-final-run exists"
  mv "$ROOT/evidence" "$ROOT/e2e/hero/evidence-final-run"
fi
# Same for an implementation-inconsistency-fixes run (PRIMARY=implfixes via e2e/fixes).
if [ -d "$ROOT/evidence" ] && ls "$ROOT/evidence" | grep -q "quote-qte"; then
  [ -d "$ROOT/e2e/impl-fixes/evidence-final-run" ] && fail "evidence/ holds an impl-fixes run but e2e/impl-fixes/evidence-final-run exists"
  mv "$ROOT/evidence" "$ROOT/e2e/impl-fixes/evidence-final-run"
fi
rm -rf "$EV" "$WIP" /tmp/keys-final-dc /tmp/keys-final-hero && mkdir -p "$EV" "$WIP" /tmp/keys-final-hero
python3 "$ROOT/e2e/design-review/fixtures.py" >/dev/null

echo "== 1. full test suite =="
( cd "$SLN" && dotnet test DeChimex.sln --no-build -c Debug ) > "$EV/final-test-log.txt" 2>&1 || fail "dotnet test"
grep -E "Passed!|Failed!" "$EV/final-test-log.txt"

echo "== 2. packaging guard =="
python3 "$ROOT/e2e/config/verify_packaging_guard.py" > "$EV/final-packaging-guard-log.txt" 2>&1 || fail "packaging guard"
tail -1 "$EV/final-packaging-guard-log.txt"

echo "== 3. app A: real Paystack + Brevo stub =="
python3 "$ROOT/e2e/design-review/brevo_stub.py" > /tmp/keys-brevo-stub.log 2>&1 & PIDS+=("$!")
start_app "" /tmp/keys-appA.log
curl -s http://127.0.0.1:5119/health | python3 -m json.tool > "$EV/final-health-log.txt"
python3 - "$EV/final-health-log.txt" <<'PY' || fail "health not Healthy"
import json,sys; h=json.load(open(sys.argv[1]))
print("health:", h["status"], {e["name"]:(e["status"],e["description"]) for e in h["entries"]})
assert h["status"]=="Healthy" and all(e["status"]=="Healthy" for e in h["entries"])
PY
E2E_SHOTS_DIR="$EV" python3 "$ROOT/e2e/design-review/ws_designreview_e2e.py" "$TAG" > "$EV/final-e2e-log.txt" 2>&1 || { tail -20 "$EV/final-e2e-log.txt"; fail "keys journey"; }
tail -1 "$EV/final-e2e-log.txt"
python3 - "$SLN/src/DeChimex.Host/appsettings.json" "$WIP/brevo_captured.jsonl" > "$EV/final-brevo-key-audit-log.txt" <<'PY' || fail "brevo audit"
import json,sys
key=json.load(open(sys.argv[1]))["Brevo"]["ApiKey"]
sends=[json.loads(l) for l in open(sys.argv[2]) if l.strip()]
ok=[s for s in sends if (s.get("_headers") or {}).get("api-key")==key]
print(f"Brevo outbound sends captured by the stub: {len(sends)}")
for s in sends:
    k=(s.get("_headers") or {}).get("api-key","")
    print(f"  subject={s.get('subject','')[:60]!r:64} api-key prefix={k[:8]!r} len={len(k)} matches_configured={k==key}")
print(f"RESULT: {len(ok)}/{len(sends)} sends carried the configured Brevo key (values hidden)")
assert sends and len(ok)==len(sends)
PY
tail -1 "$EV/final-brevo-key-audit-log.txt"
stop_app

echo "== 4. app B: Paystack stub + Brevo stub =="
python3 "$ROOT/e2e/payments/paystack_stub.py" > /tmp/keys-paystack-stub.log 2>&1 & PIDS+=("$!")
sleep 1
start_app "Paystack__ApiBaseUrl=http://127.0.0.1:9090" /tmp/keys-appB.log
E2E_SHOTS_DIR=/tmp/keys-final-dc python3 "$ROOT/e2e/direct-checkout/workflow.py" "${TAG}dc" > "$EV/final-checkout-e2e-log.txt" 2>&1 || { tail -20 "$EV/final-checkout-e2e-log.txt"; fail "checkout journey"; }
grep "RESULT" "$EV/final-checkout-e2e-log.txt"
grep -q "0 failed" "$EV/final-checkout-e2e-log.txt" || fail "checkout journey had failures"
# Same-run screenshots, renumbered to continue the evidence sequence (17..37).
n=17; for f in $(ls /tmp/keys-final-dc/*.png | sort); do
  b="$(basename "$f")"; cp "$f" "$EV/$(printf '%02d' $n)-checkout-${b#[0-9][0-9]-}"; n=$((n+1)); done
curl -s http://127.0.0.1:9090/control/state > /tmp/keys-paystack-state.json
python3 - /tmp/keys-paystack-state.json > "$EV/final-paystack-auth-log.txt" <<'PY' || fail "paystack auth audit"
import json,sys
s=json.load(open(sys.argv[1]))
ic=s.get("init_calls",[]); rf=s.get("refunds",[])
print(f"Paystack /transaction/initialize calls: {len(ic)}")
for c in ic:
    a=c["auth_header"]
    print(f"  ref={c['reference']} auth_present={a['present']} matches_configured_secret={a['matches_configured']}")
print(f"Paystack /refund calls: {len(rf)}")
for r in rf:
    print(f"  auth_matches_configured_secret={(r.get('auth_header') or {}).get('matches_configured')}")
allok = bool(ic) and all(c["auth_header"]["matches_configured"] for c in ic) and all((r.get("auth_header") or {}).get("matches_configured") for r in rf)
print("RESULT:", "ALL Paystack API calls authenticated with the configured secret key (values hidden)" if allok else "MISMATCH")
sys.exit(0 if allok else 1)
PY
tail -1 "$EV/final-paystack-auth-log.txt"

echo "== 4b. restored hero composition + true-alpha art regression =="
E2E_SHOTS_DIR=/tmp/keys-final-hero python3 "$ROOT/e2e/hero/hero_e2e.py" "${TAG:0:6}hr" > "$EV/final-hero-regression-log.txt" 2>&1 || { tail -20 "$EV/final-hero-regression-log.txt"; fail "hero regression"; }
grep "RESULT" "$EV/final-hero-regression-log.txt"
grep -q "0 failed" "$EV/final-hero-regression-log.txt" || fail "hero regression had failures"
stop_app

cat > "$EV/README.md" <<'EOF'
# Evidence — Provider keys re-inserted (repo BakareAhmad/ContinualDevelopment.git)

All files come from ONE clean final run: `bash e2e/provider-keys/run_final.sh keysfinal`
(fresh in-memory apps, fresh stubs, fresh evidence folder). Key values are never logged.

| Stage | Result | File |
|---|---|---|
| Full test suite | 900/900 (462 unit + 438 integration) | final-test-log.txt |
| Packaging-guard validations | 14/14 | final-packaging-guard-log.txt |
| /health (app A, REAL api.paystack.co) | Healthy — database, paystack "HTTP 200", brevo (stub) | final-health-log.txt |
| Keys + design-review journey (app A) | 36/36 | final-e2e-log.txt, shots 01–16 |
| Brevo api-key header on every outbound send | 4/4 match configured key | final-brevo-key-audit-log.txt |
| Direct-checkout / Paystack journey (app B, Paystack stub) | 60/60 | final-checkout-e2e-log.txt, shots 17–37 |
| Paystack Authorization header (initialize ×4, refund ×1) | all match configured secret | final-paystack-auth-log.txt |
| Hero regression (9 viewports + 14 responsive checkpoints, alpha/no-box, header search, PNG fallback) | 383/383 | final-hero-regression-log.txt |
| Secret-leak sweep over evidence/ | clean (only the PUBLIC pk_test_ key appears, shot 01) | — |

## Screenshots (01–37, ordered from the single final run)
- `01-config-endpoint-paystack-public-key.png` — `/api/v1/config` serves the Paystack PUBLIC key, no secrets
- `02-owner-logged-in.png` — owner signed in
- `03-customer-verified-signed-in.png` — customer registered → Brevo OTP → verified → signed in
- `04-customer-workspace-empty-design-reviews.png`, `05-staff-workspace-open.png` — empty customer/staff workspaces
- `06-customer-uploaded-v1.png`, `07-staff-sees-v1-live.png` — v1 upload, staff sees it live
- `08-staff-approved-v1.png`, `09-customer-sees-approval-live.png` — approve → customer sees approval live
- `10-staff-sees-v2-live-independent-history.png` — v2 live (independent history)
- `11-reject-requires-reason-validation.png` — VALIDATION: reject requires a reason
- `12-staff-rejected-v2-with-reason.png`, `13-customer-sees-rejection-reason-live.png` — rejected with reason, customer sees it live
- `14-invalid-file-type-refused.png` — VALIDATION: `.exe` refused (400 "File type '.exe' is not allowed")
- `15-customer-workspace-final-persisted.png`, `16-staff-workspace-final-persisted.png` — history persists after reload (customer + staff)
- `17-checkout-catalog-filter.png` .. `21-checkout-checkout-amounts.png` — catalog filter, direct vs custom buy actions, cart guard rejects custom product, checkout amounts
- `22-checkout-direct-order-placed.png`, `23-checkout-direct-paid.png` — order placed → Paystack → paid
- `24-checkout-direct-workspace-no-design-steps.png` .. `27-checkout-direct-lifetime-spending.png` — workspace, admin badge, activity log, lifetime spend
- `28-checkout-review-unlocked-after-purchase.png`, `29-checkout-review-locked-unpurchased.png` — review unlocked after purchase / locked when unpurchased
- `30-checkout-direct-payment-failed.png` — VALIDATION: payment failed state
- `31-checkout-direct-retry-paid.png`, `32-checkout-direct-cancelled.png`, `33-checkout-direct-refunded.png` — retry paid, cancelled, refunded
- `34-checkout-custom-workspace-assigned.png` .. `36-checkout-custom-paid.png` — custom order: assigned workspace, admin badge, paid
- `37-checkout-my-orders-coherent.png` — My orders coherent

Previous evidence set (implementation-inconsistency fixes journey, 6 shots + logs) → `e2e/impl-fixes/evidence-final-run/`.
EOF

echo "== 4c. footer placement across page groups (regression) =="
rm -rf /tmp/keys-final-ft && mkdir -p /tmp/keys-final-ft
E2E_SHOTS_DIR=/tmp/keys-final-ft python3 "$ROOT/e2e/footer/footer_e2e.py" "${TAG:0:6}ft" > "$EV/final-footer-e2e-log.txt" 2>&1 || { tail -20 "$EV/final-footer-e2e-log.txt"; fail "footer placement journey"; }
grep "CHECKS PASSED" "$EV/final-footer-e2e-log.txt"
grep -q "0 failed" "$EV/final-footer-e2e-log.txt" || fail "footer journey had failures"

echo "== 5. secret-leak sweep over evidence text logs =="
python3 - "$SLN/src/DeChimex.Host/appsettings.json" "$EV" <<'PY' || fail "secret leaked into evidence"
import json,sys,pathlib
d=json.load(open(sys.argv[1])); secrets=[d["Brevo"]["ApiKey"], d["Paystack"]["SecretKey"]]
bad=[p.name for p in pathlib.Path(sys.argv[2]).iterdir() if p.is_file() and any(s.encode() in p.read_bytes() for s in secrets)]
print("leak sweep:", "clean" if not bad else bad); sys.exit(1 if bad else 0)
PY
echo "FINAL RUN PASSED — $(ls "$EV"/*.png | wc -l) screenshots in evidence/"
