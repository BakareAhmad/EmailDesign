# Design-proof lifecycle E2E

`ws_proof_e2e.py` drives the full journey in a real browser (Playwright/Chromium)
against a sandbox app and asserts 44 checks, writing ordered full-page evidence
to `/home/user/e2e-proof-wip/final-shots/`:

1. owner login → category → ordering question → customizable product
2. customer register → verify (token captured from `brevo_stub.py`) → order request
3. staff confirm → upload proof v1 → customer requests changes (feedback kept)
4. staff notified (`Proof changes requested`) → upload proof v2
5. customer sends chat photo + video (proof card stays exactly v1+v2)
6. customer approves v2 (staff live toast) → second customer gets 404
7. staff finalize → mark paid → production → ready → completed
8. both workspaces keep full proof/decision/chat/timeline history after completion

Run:

```bash
# Biome: app on :5119 (Development + Database__UseInMemory=true +
#   Brevo__ApiBaseUrl=http://127.0.0.1:9999), stub on :9999, fixtures built.
python3 brevo_stub.py &            # capture verification emails + tokens
python3 fixtures.py                # product/proof/chat PNGs (needs PIL)
python3 ws_proof_e2e.py <TAG>      # TAG-unique catalog/customer per run
```

`TAG` must be unique per run against a persistent app (category/product names
and customer emails embed it). `clip.mp4` (chat video fixture) is downloaded
separately — see the header of `ws_proof_e2e.py`.
