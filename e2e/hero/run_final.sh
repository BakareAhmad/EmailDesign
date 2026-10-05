#!/usr/bin/env bash
# Hero restore (pre-refactor composition + true-alpha art) — ONE clean final run -> evidence/.
# Same pipeline as e2e/fixes/run_final.sh with the hero journey (vs e2e/hero/reference) as primary evidence
# (brand, checkout, design review, fixes, dev portal and dev sign-in run as regressions).
PRIMARY=hero exec bash "$(cd "$(dirname "$0")" && pwd)/../fixes/run_final.sh" "$@"
