> Historical verification record. For the current wishlist release, see [wishlist verification](../wishlist/VERIFICATION.md).

# Final verification — 22 September 2026

## Results

| Verification | Result |
|---|---|
| Complete solution build | Passed, zero errors; inherited warnings remain |
| Complete automated suite | **609 passed, 0 failed, 0 skipped** |
| Unit suite | 325 passed |
| Integration suite | 284 passed |
| Activity browser journey | **42 named checks passed** |
| Category/ordering browser regression | **51 checks passed** |
| Design-review browser regression | **36 checks passed** |
| Separate-process relational persistence probe | Write process committed aggregate/activity; read process recovered the row and executed authorization/relationship-filter queries |
| Final evidence | **25 ordered original screenshots**, screenshots only in `evidence/` |

The complete browser run used a newly started sandbox host with fresh InMemory business data. Its run tags are `releaseact`, `releasecat`, `releaserev`. All promoted images come from these three sequential journeys on that final host. Failed/debugging runs and previous-feature screenshots were removed from the deliverable evidence directory.

## What was exercised

- Two independent authenticated Owner browser sessions receive actual SignalR invalidation frames and recover the same persisted order activity without duplicates.
- Historical-only product price changes do not emit business live hints or automatically appear in the normal live window; explicit refresh reveals the actual before/after price. Identical edits remain silent.
- Customer and default Manager access denial; Owner delegation of activity.view; an existing Manager cookie gains access; revocation clears the open feed; disabled-account and stale-cookie denial in integration tests.
- Actual customer registration/verification, catalog setup, rejected >10,000 quantity submission, order creation and automatic sales assignment.
- Design upload/approval live activity; finalization and pricing, bank-transfer confirmation, production/ready/completion, manual refund and repeated-refund no-duplicate behavior.
- A genuinely disconnected second session recovers committed permission history on SignalR reconnection.
- Combined filters, human-readable product search, safe existing order link, empty state, reversed date validation, malformed ID rejection, persisted IDs across refresh, and absence of private payment notes/customer email/token fields.
- Real SQLite constraints: duplicate business write failure leaves no success audit; corrected same-context retry produces one row; outer rollback removes both business/audit changes; duplicate SourceKey violates the unique index.
- Stable domain-event identity, owned price no-ops, privacy allowlisting, repeated signed webhook/verification canonical success, malformed/failed gateway refund responses, invalid refund state before external calls, accepted-first/failed-second refund checkpoint with safe retry, unchanged role/option-set noise suppression, bounded pagination and relationship-query translation.
- Existing major-category relationships, moves, unassignment, delete blockers, search/sort/filter combinations, required ordering answers, portal shopping and mobile navigation.
- Existing design-review required file/rejection reason, invalid file-type refusal, live independent decision history, participant notifications/timeline and refresh persistence.

One existing request-logging line labels a thrown upload validation exception as 500 before the outer exception handler runs. Inspection confirmed the final HTTP response is **400** with the expected validation page; the browser regression verifies the refusal. No corresponding false design-upload activity is created. No unhandled business failure was treated as a successful final check.

## Reproduction

From the repository root with .NET 9, Python Playwright/Pillow and Chromium installed:

```sh
dotnet build DeChimexPrints/DeChimex.sln
dotnet test DeChimexPrints/DeChimex.sln --no-build
python e2e/design-review/fixtures.py
```

Start the local email test service in one terminal:

```sh
python e2e/design-review/brevo_stub.py
```

Start a **fresh** sandbox app in another terminal (the test host should not point to production data):

```sh
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://0.0.0.0:5119 \
Database__UseInMemory=true \
Brevo__ApiBaseUrl=http://127.0.0.1:9999 \
dotnet run --no-launch-profile --project DeChimexPrints/src/DeChimex.Host
```

Run the three journeys in order, using new tags and isolated output directories:

```sh
E2E_SHOTS_DIR=/tmp/activity-final/activity python e2e/activity/workflow.py uniqueact
E2E_SHOTS_DIR=/tmp/activity-final/major python e2e/major-categories/workflow.py uniquecat
E2E_SHOTS_DIR=/tmp/activity-final/review python e2e/design-review/ws_designreview_e2e.py uniquerev
```

The helpers retain the existing sandbox fixture paths under `/home/user/e2e-dr-wip`. Create that directory if necessary. The Brevo stub captures local test email, including verification material; do not publish its capture file. The design-review regression also performs the pre-existing read-only Paystack test-key authorization probe; no real payment/refund transfer is claimed by these browser tests.

To independently verify cross-process persistence, use a new SQLite file:

```sh
dotnet run --project e2e/activity/persistence -- write /tmp/activity-proof.db
dotnet run --no-build --project e2e/activity/persistence -- read /tmp/activity-proof.db
```

This harness uses the production DbContext, audit interceptor and read query. Its only provider adaptation changes SQL Server `nvarchar(max)` DDL to SQLite TEXT. It is verification code, not a production database-provider change.

## Limits and deployment checks

- Browser flows ran against the existing InMemory preview mode. Relational transaction/uniqueness/restart checks used actual SQLite files, not InMemory assertions.
- The SQL Server migration and idempotent upgrade script were generated successfully. No production SQL Server was available to apply the upgrade or scrub an actual customer's historical database; run the supplied upgrade against a protected staging copy before production.
- Gateway acceptance/failure/malformed-response and multi-payment retry cases used deterministic gateway responses. They do not establish actual external settlement.
- Concurrent external refunds, ambiguous provider-success/local-save-failure and multi-node SignalR topology are not claimed as exhaustively verified or solved.

Plain-text final summaries accompany this document: `automated-tests.txt`, `browser-activity.txt`, `browser-category-regression.txt`, `browser-design-regression.txt`, `process-persistence.txt`.
