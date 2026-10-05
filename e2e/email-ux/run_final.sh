#!/usr/bin/env bash
# Email design system — ONE clean final run -> evidence/.
# Same pipeline as e2e/fixes/run_final.sh with the email design-system journey
# (e2e/email-ux/email_e2e.py, stage 8h) as primary evidence: all 19 rendered
# templates, every live send captured by the Brevo stub during the run, the
# remaining template drives (forgot password, contact, staff reply, provider test,
# workspace message, design proof) and the app surfaces side by side. Checkout,
# brand, design-review, fixes, dev-portal, dev-abort, PWA, placeholder,
# impl-fixes and footer journeys all run as regressions.
PRIMARY=emailux exec bash "$(cd "$(dirname "$0")" && pwd)/../fixes/run_final.sh" "$@"
