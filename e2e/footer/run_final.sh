#!/usr/bin/env bash
# Footer placement — ONE clean final run -> evidence/.
# Same pipeline as e2e/fixes/run_final.sh with the footer placement journey
# (e2e/footer/footer_e2e.py, stage 8g) as primary evidence (brand, checkout,
# design review, fixes, dev portal, dev sign-in, PWA and placeholder journeys
# run as regressions).
PRIMARY=footer exec bash "$(cd "$(dirname "$0")" && pwd)/../fixes/run_final.sh" "$@"
