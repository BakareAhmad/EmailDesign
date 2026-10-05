#!/usr/bin/env bash
# ============================================================================
# ONE clean final run -> evidence/ (repo root). Shared by three harnesses:
#   bash e2e/fixes/run_final.sh          PRIMARY=fixes    (fixes journey shots -> evidence/)
#   bash e2e/brand/run_final.sh          PRIMARY=brand    (brand/responsive shots -> evidence/,
#                                                          fixes shots -> e2e/fixes/evidence-final-run)
#   bash e2e/dev-abort/run_final.sh      PRIMARY=devabort (dev sign-in / dropped-request shots -> evidence/;
#                                                          every other journey runs as a regression)
#   bash e2e/hero/run_final.sh           PRIMARY=hero     (restored hero vs reference shots -> evidence/)
#   bash e2e/email-ux/run_final.sh       PRIMARY=emailux  (email design-system shots -> evidence/;
#                                                          every other journey runs as a regression)
#   bash e2e/footer/run_final.sh         PRIMARY=footer   (footer placement shots -> evidence/;
#   PRIMARY=pwa bash e2e/fixes/run_final.sh               (PWA shots -> DeChimexPrints/docs/evidence/pwa,
#                                                          dev-abort journey + all others run as regressions)
# A previous primary's evidence/ is moved to its harness folder first (never mixed).
# Stages:
#   1. dotnet test (full suite)                                  -> final-test-log.txt
#   2. app (in-memory DB, Paystack stub :9090, Brevo stub :9999), public /health
#   3. developer HTTP smoke                                      -> final-dev-smoke-log.txt
#   4. direct-checkout regression (real payments)                -> final-checkout-regression-log.txt
#   5. brand images + responsive journey (needs 4's invoices)   -> final-brand-e2e-log.txt
#   6. design-review regression                                  -> final-design-review-regression-log.txt
#   7. fixes journey (notification audience, error links, cards) -> final-fixes-e2e-log.txt
#   8. developer-portal regression                               -> final-dev-portal-regression-log.txt
#  8b. dev sign-in + dropped requests + blank-404 fix           -> final-dev-abort-e2e-log.txt
#      (primary evidence when PRIMARY=devabort; regression with shots in /tmp otherwise)
#  8c. [hero] restored hero composition + true-alpha art       -> final-hero-e2e-log.txt
#  8d. PWA + app-like mobile journey                            -> final-pwa-e2e-log.txt
#      (primary evidence when PRIMARY=pwa: 12 shots -> DeChimexPrints/docs/evidence/pwa)
#  8e. placeholder & generated-string format verification        -> final-placeholder-audit-log.txt
#      (primary evidence when PRIMARY=placeholders: 5 ordered shots -> evidence/)
#  8f. implementation-inconsistency fixes journey                 -> final-impl-fixes-log.txt
#      (primary evidence when PRIMARY=implfixes: 6 ordered shots -> evidence/)
#  8g. footer placement across page groups (public / customer / app / admin / auth)
#      -> final-footer-e2e-log.txt
#      (primary evidence when PRIMARY=footer: 26 ordered shots -> evidence/)
#  8h. email design system: all 19 templates, the live sends captured by the Brevo
#      stub during this run, the remaining template drives (forgot password, contact,
#      reply, provider test, workspace message, design proof) and the app side by side
#      -> final-email-e2e-log.txt
#      (primary evidence when PRIMARY=emailux: ordered shots -> evidence/)
#   9. app-log error count + secret-leak sweep over every evidence file
# Dry run: EV_DIR=/var/tmp/dry SKIP_TESTS=1 bash e2e/brand/run_final.sh
# Build first: dotnet build DeChimex.sln -c Debug.
# ============================================================================
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SLN="$ROOT/DeChimexPrints"
EV="${EV_DIR:-$ROOT/evidence}"
PRIMARY="${PRIMARY:-fixes}"
WIP=/home/user/e2e-dr-wip
TAG="${1:-final}"
export DOTNET_ROOT=/var/tmp/dotnet PATH=/var/tmp/dotnet:$PATH NUGET_PACKAGES=/var/tmp/nuget \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PLAYWRIGHT_BROWSERS_PATH=/var/tmp/ms-playwright
PIDS=()
cleanup() {
  for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done
  pkill -f "DeChimex[.]Host" 2>/dev/null; wait 2>/dev/null
}
trap cleanup EXIT
fail() { echo "FINAL RUN FAILED: $*"; exit 1; }
wait_port() { for _ in $(seq 1 90); do curl -s -o /dev/null "http://127.0.0.1:$1/" && return 0; sleep 1; done; return 1; }
# Harnesses end with either "RESULT: N passed, 0 failed" or (design-review) "ALL N CHECKS PASSED".
result() { grep -E "RESULT|CHECKS PASSED" "$1" | tail -1; grep -qE " 0 failed|^ALL [0-9]+ CHECKS PASSED" "$1" || fail "$2 had failures"; }

# ---- clean slate ----
pkill -f "brevo_stub[.]py" 2>/dev/null; pkill -f "paystack_stub[.]py" 2>/dev/null; pkill -f "DeChimex[.]Host" 2>/dev/null; sleep 2
# A previous task's final evidence is preserved under its own harness, never mixed with this
# run. Each archive is identified by a shot name unique to its primary journey (a stage LOG
# name is not unique: every full run writes every regression log), and is only moved when this
# run is for a DIFFERENT harness.
archive_previous() { # <primary-that-owns-it> <marker-file> <archive-dir>
  local owner="$1" marker="$2" dest="$3"
  [ -z "${EV_DIR:-}" ] && [ "$PRIMARY" != "$owner" ] && [ -d "$ROOT/evidence" ] \
    && ls "$ROOT/evidence" | grep -q "$marker" || return 0
  [ -d "$ROOT/$dest" ] && fail "evidence/ holds a $owner run but $dest exists"
  mv "$ROOT/evidence" "$ROOT/$dest"
}
archive_previous fixes        "customer-design-review-waiting"  e2e/fixes/evidence-final-run
archive_previous brand        "home-hero-320px"                 e2e/brand/evidence-final-run
archive_previous devabort     "01-developer-sign-in-page"       e2e/dev-abort/evidence-final-run
archive_previous hero         "hero-vs-reference"               e2e/hero/evidence-final-run
archive_previous keys         "01-dev-login-page"               e2e/provider-keys/evidence-final-run
archive_previous footer       "01-public-home-full-footer-mobile" e2e/footer/evidence-final-run
archive_previous emailux      "app-reference-home"              e2e/email-ux/evidence-final-run
rm -rf "$EV" "$WIP" /tmp/fx-ab-shots /tmp/fx-dr-shots /tmp/fx-dp-shots /tmp/fx-dc-shots /tmp/fx-fixes-shots && mkdir -p "$EV" "$WIP"
python3 "$ROOT/e2e/design-review/fixtures.py" >/dev/null

if [ -z "${SKIP_TESTS:-}" ]; then
  echo "== 1. full test suite =="
  # -m:1 runs the two test projects sequentially: this sandbox has ~2 GB RAM and no swap,
  # so two parallel test hosts can crash the VSTest host (environmental, not a code failure).
  ( cd "$SLN" && dotnet test DeChimex.sln --no-build -c Debug -m:1 ) > "$EV/final-test-log.txt" 2>&1 || { grep -E "Failed|\[FAIL\]|Aborted" "$EV/final-test-log.txt" | head; fail "dotnet test"; }
  grep -E "Passed!|Failed!" "$EV/final-test-log.txt"
fi

echo "== 2. app with Paystack + Brevo stubs =="
python3 "$ROOT/e2e/design-review/brevo_stub.py" > /tmp/fx-brevo-stub.log 2>&1 & PIDS+=("$!")
python3 "$ROOT/e2e/payments/paystack_stub.py" > /tmp/fx-paystack-stub.log 2>&1 & PAYSTACK_PID=$!; PIDS+=("$PAYSTACK_PID")
sleep 1
( cd "$SLN" && env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:5119 \
    Database__UseInMemory=true Brevo__ApiBaseUrl=http://127.0.0.1:9999 Paystack__ApiBaseUrl=http://127.0.0.1:9090 \
    dotnet run --project src/DeChimex.Host --no-build --no-launch-profile -c Debug ) > /tmp/fx-app.log 2>&1 &
PIDS+=("$!"); wait_port 5119 || fail "app did not start"
sleep 3
curl -s http://127.0.0.1:5119/health | python3 -m json.tool > "$EV/final-health-log.txt"
python3 - "$EV/final-health-log.txt" <<'PY' || fail "public /health not Healthy"
import json,sys; h=json.load(open(sys.argv[1]))
print("public /health:", h["status"], {e["name"]: e["status"] for e in h["entries"]})
assert h["status"] == "Healthy"
PY

echo "== 3. developer smoke =="
python3 "$ROOT/e2e/dev-portal/smoke.py" > "$EV/final-dev-smoke-log.txt" 2>&1 || { tail -20 "$EV/final-dev-smoke-log.txt"; fail "dev smoke"; }
tail -1 "$EV/final-dev-smoke-log.txt"

echo "== 4. checkout regression (generates real payments) =="
E2E_SHOTS_DIR=/tmp/fx-dc-shots python3 "$ROOT/e2e/direct-checkout/workflow.py" "${TAG:0:6}dc" > "$EV/final-checkout-regression-log.txt" 2>&1 \
  || { tail -20 "$EV/final-checkout-regression-log.txt"; fail "checkout regression"; }
result "$EV/final-checkout-regression-log.txt" "checkout regression"

echo "== 5. brand images + responsive journey =="
BR_SHOTS=/tmp/fx-br-shots; [ "$PRIMARY" = "brand" ] && BR_SHOTS="$EV"
rm -rf /tmp/fx-br-shots
{
  E2E_SHOTS_DIR="$BR_SHOTS" python3 "$ROOT/e2e/brand/brand_e2e.py" "${TAG:0:6}br" > "$EV/final-brand-e2e-log.txt" 2>&1 \
    || { grep -E "FAIL|Error|Traceback" "$EV/final-brand-e2e-log.txt" | head -30; fail "brand journey"; }
  result "$EV/final-brand-e2e-log.txt" "brand journey"
}

echo "== 6. design-review regression =="
E2E_SHOTS_DIR=/tmp/fx-dr-shots python3 "$ROOT/e2e/design-review/ws_designreview_e2e.py" "${TAG:0:6}dr" > "$EV/final-design-review-regression-log.txt" 2>&1 \
  || { grep -E "FAIL|Error|Traceback" "$EV/final-design-review-regression-log.txt" | head -20; fail "design-review regression"; }
result "$EV/final-design-review-regression-log.txt" "design-review regression"

echo "== 7. fixes journey =="
# PRIMARY=brand: the fixes journey's screenshots from THIS run become the fixes harness's own evidence
# (e2e/fixes/evidence-final-run); dry runs (EV_DIR set) keep them in /tmp.
FX_SHOTS="$EV"
if [ "$PRIMARY" != "fixes" ]; then
  if [ -z "${EV_DIR:-}" ] && [ "$PRIMARY" = "brand" ]; then FX_SHOTS="$ROOT/e2e/fixes/evidence-final-run"; rm -rf "$FX_SHOTS"; else FX_SHOTS=/tmp/fx-fixes-shots; fi
fi
E2E_SHOTS_DIR="$FX_SHOTS" python3 "$ROOT/e2e/fixes/fixes_e2e.py" "${TAG:0:6}fx" > "$EV/final-fixes-e2e-log.txt" 2>&1 \
  || { grep -E "FAIL|Error|Traceback" "$EV/final-fixes-e2e-log.txt" | head -30; fail "fixes journey"; }
result "$EV/final-fixes-e2e-log.txt" "fixes journey"

echo "== 8. developer-portal regression =="
E2E_SHOTS_DIR=/tmp/fx-dp-shots PAYSTACK_STUB_PID="$PAYSTACK_PID" python3 "$ROOT/e2e/dev-portal/dev_portal_e2e.py" "${TAG:0:6}dp" \
  > "$EV/final-dev-portal-regression-log.txt" 2>&1 || { grep -E "FAIL|Error|Traceback" "$EV/final-dev-portal-regression-log.txt" | head -30; fail "dev portal regression"; }
tail -1 "$EV/final-dev-portal-regression-log.txt"
ls /tmp/fx-dp-shots/*.csv 2>/dev/null | head -1 | xargs -r -I{} cp {} "$EV/final-service-fee-export.csv"

echo "== 8b. developer sign-in + dropped-request journey =="
{
  AB_SHOTS=/tmp/fx-ab-shots; [ "$PRIMARY" = "devabort" ] && AB_SHOTS="$EV"
  APP_LOG=/tmp/fx-app.log E2E_SHOTS_DIR="$AB_SHOTS" python3 "$ROOT/e2e/dev-abort/dev_abort_e2e.py" "${TAG:0:6}ab" \
    > "$EV/final-dev-abort-e2e-log.txt" 2>&1 || { grep -E "FAIL|Error|Traceback" "$EV/final-dev-abort-e2e-log.txt" | head -30; fail "dev sign-in / dropped-request journey"; }
  result "$EV/final-dev-abort-e2e-log.txt" "dev sign-in / dropped-request journey"
}

echo "== 8d. PWA + app-like mobile journey =="
{
  # PRIMARY=pwa publishes the 12 ordered evidence shots to docs/evidence/pwa;
  # any other primary runs the journey as a regression with shots in /tmp.
  PWA_SHOTS=/tmp/fx-pwa-shots; [ "$PRIMARY" = "pwa" ] && PWA_SHOTS="$ROOT/DeChimexPrints/docs/evidence/pwa"
  rm -rf "$PWA_SHOTS" /home/user/e2e-pwa-wip/profile
  E2E_SHOTS_DIR="$PWA_SHOTS" python3 "$ROOT/e2e/pwa/pwa_e2e.py" "${TAG:0:6}pwa" > "$EV/final-pwa-e2e-log.txt" 2>&1 \
    || { grep -E "FAIL|Error|Traceback" "$EV/final-pwa-e2e-log.txt" | head -30; fail "pwa journey"; }
  result "$EV/final-pwa-e2e-log.txt" "pwa journey"
}

echo "== 8e. placeholder & generated-string format verification =="
{
  PH_SHOTS=/tmp/fx-ph-shots; [ "$PRIMARY" = "placeholders" ] && PH_SHOTS="$EV"
  [ "$PH_SHOTS" = "$EV" ] || rm -rf "$PH_SHOTS"
  E2E_SHOTS_DIR="$PH_SHOTS" python3 "$ROOT/e2e/placeholder-audit/placeholder_e2e.py" "${TAG:0:6}ph" > "$EV/final-placeholder-audit-log.txt" 2>&1 \
    || { grep -E "FAIL|Error|Traceback" "$EV/final-placeholder-audit-log.txt" | head -30; fail "placeholder audit journey"; }
  result "$EV/final-placeholder-audit-log.txt" "placeholder audit journey"
}

echo "== 8f. implementation-inconsistency fixes journey =="
{
  IF_SHOTS=/tmp/fx-if-shots; [ "$PRIMARY" = "implfixes" ] && IF_SHOTS="$EV"
  [ "$IF_SHOTS" = "$EV" ] || rm -rf "$IF_SHOTS"
  E2E_SHOTS_DIR="$IF_SHOTS" python3 "$ROOT/e2e/impl-fixes/impl_fixes_e2e.py" "${TAG:0:6}if" > "$EV/final-impl-fixes-log.txt" 2>&1 \
    || { grep -E "FAIL|Error|Traceback" "$EV/final-impl-fixes-log.txt" | head -30; fail "impl-fixes journey"; }
  result "$EV/final-impl-fixes-log.txt" "impl-fixes journey"
}

echo "== 8g. footer placement across page groups =="
{
  FT_SHOTS=/tmp/fx-ft-shots; [ "$PRIMARY" = "footer" ] && FT_SHOTS="$EV"
  [ "$FT_SHOTS" = "$EV" ] || rm -rf "$FT_SHOTS"
  E2E_SHOTS_DIR="$FT_SHOTS" python3 "$ROOT/e2e/footer/footer_e2e.py" "${TAG:0:6}ft" > "$EV/final-footer-e2e-log.txt" 2>&1 \
    || { grep -E "FAIL|Error|Traceback" "$EV/final-footer-e2e-log.txt" | head -30; fail "footer placement journey"; }
  result "$EV/final-footer-e2e-log.txt" "footer placement journey"
}

echo "== 8h. email design system (renderer, live sends, template drives, app reference) =="
{
  EM_SHOTS=/tmp/fx-em-shots
  if [ "$PRIMARY" = "emailux" ]; then
    EM_SHOTS="$EV"
    # Primary evidence: the rendered previews must come from this run's renderer, so
    # regenerate them from the freshly built binaries before the harness reads them.
    rm -rf "$EV/email-previews" && mkdir -p "$EV/email-previews"
    ( cd "$SLN" && EMAIL_PREVIEW_DIR="$EV/email-previews" dotnet test tests/DeChimex.UnitTests --no-build -c Debug \
        --filter "FullyQualifiedName~EmailTemplateRendererTests" ) >> "$EV/final-email-e2e-log.txt" 2>&1
    EM_PREVIEWS="$EV/email-previews"
  else
    rm -rf "$EM_SHOTS"
    ( cd "$SLN" && EMAIL_PREVIEW_DIR="$ROOT/email-previews" dotnet test tests/DeChimex.UnitTests --no-build -c Debug \
        --filter "FullyQualifiedName~EmailTemplateRendererTests" ) > /tmp/fx-em-previews.log 2>&1
    EM_PREVIEWS="$ROOT/email-previews"
  fi
  E2E_PREVIEW_DIR="$EM_PREVIEWS" E2E_SHOTS_DIR="$EM_SHOTS" python3 "$ROOT/e2e/email-ux/email_e2e.py" "${TAG:0:6}em" >> "$EV/final-email-e2e-log.txt" 2>&1 \
    || { grep -E "FAIL|Error|Traceback" "$EV/final-email-e2e-log.txt" | head -30; fail "email design journey"; }
  result "$EV/final-email-e2e-log.txt" "email design journey"
}

if [ "$PRIMARY" = "hero" ]; then
  echo "== 8c. restored hero composition + true-alpha art (vs reference) =="
  E2E_SHOTS_DIR="$EV" python3 "$ROOT/e2e/hero/hero_e2e.py" "${TAG:0:6}hr" > "$EV/final-hero-e2e-log.txt" 2>&1 \
    || { grep -E "FAIL|Error|Traceback" "$EV/final-hero-e2e-log.txt" | head -30; fail "hero restore journey"; }
  result "$EV/final-hero-e2e-log.txt" "hero restore journey"
fi

echo "== 9. app log + secret-leak sweep =="
ERRS=$(grep -c "Unhandled exception" /tmp/fx-app.log); echo "app log unhandled exceptions: $ERRS" | tee "$EV/final-app-log-summary.txt"
[ "$ERRS" = "0" ] || fail "unhandled exceptions in app log"
python3 - "$SLN/src/DeChimex.Host/appsettings.json" "$EV" <<'PY' || fail "secret leaked into evidence"
import json,sys,pathlib
d=json.load(open(sys.argv[1])); secrets=[d["Brevo"]["ApiKey"], d["Paystack"]["SecretKey"]]
bad=[p.name for p in pathlib.Path(sys.argv[2]).iterdir() if p.is_file() and any(s.encode() in p.read_bytes() for s in secrets)]
print("leak sweep:", "clean" if not bad else bad); sys.exit(1 if bad else 0)
PY
echo "FINAL RUN PASSED — $(ls "$EV"/*.png | wc -l) screenshots in $EV"
