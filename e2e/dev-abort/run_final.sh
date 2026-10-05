#!/usr/bin/env bash
# Developer Portal sign-in + dropped-request fix — ONE clean final run -> evidence/.
# Same pipeline as e2e/fixes/run_final.sh with the dev-abort journey as primary evidence
# (brand images, checkout, design review, fixes and dev portal run as regressions).
PRIMARY=devabort exec bash "$(cd "$(dirname "$0")" && pwd)/../fixes/run_final.sh" "$@"
