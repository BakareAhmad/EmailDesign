#!/usr/bin/env bash
# Brand images + responsive layout — ONE clean final run -> evidence/.
# Same pipeline as e2e/fixes/run_final.sh with the brand journey as primary evidence.
PRIMARY=brand exec bash "$(cd "$(dirname "$0")" && pwd)/../fixes/run_final.sh" "$@"
