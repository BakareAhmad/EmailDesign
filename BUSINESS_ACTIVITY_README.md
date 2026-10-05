# Admin Business Activity Audit Log

Implemented 22 September 2026. Open **Admin → Business activity** (`/admin/activity`).

## What ships

- A persistent, permission-protected operational feed built on the existing `AuditLogs` and SaveChanges interceptor—not a new event store or a copy of chat.
- Typed, compact activity for supported order, payment, refund, design, inventory, catalog, customer, review, staff and permission changes.
- Selective live invalidation through the existing NotificationHub, with authorized history refetch, reconnect recovery and periodic fallback.
- Category/type/date/actor/order/customer/product/staff/importance filters, bounded search, pagination and permission-aware entity links.
- `activity.view`: Owner by default; delegable through the existing role checklist. Current database grants and account state govern access, including already-open sessions.
- Approved historical snapshot sanitization and a strict forward snapshot allowlist.
- Truthful refund recording: gateway request acceptance is explicitly **not** confirmation of final settlement. Manual refunds are explicitly staff records.

## Verification

**609 automated tests passed:** 325 unit + 284 integration. **129 named browser acceptance checks passed:** 42 activity + 51 category/ordering regression + 36 design-review regression. Relational transaction failure/retry/rollback and uniqueness tests passed; a separate process wrote activity to SQLite and a second process recovered it using the production query implementation.

The original activity-release ZIP contained **25 ordered screenshots** from its clean browser run. In this later wishlist release, `evidence/` contains the new clean-run screenshots described in [WISHLIST_FIX_README.md](WISHLIST_FIX_README.md). See [evidence index](docs/business-activity/EVIDENCE-INDEX.md) and [verification details](docs/business-activity/VERIFICATION.md).

## Deploy

1. Back up the database and restrict access to that backup: old snapshots may contain sensitive values.
2. Review/apply migration `20260922040450_AddBusinessActivityAudit`, or the idempotent upgrade script [`database/20260922-BusinessActivity-Upgrade.sql`](database/20260922-BusinessActivity-Upgrade.sql) on the preceding major-category schema. SQL Server 2017+ with database compatibility level 130+ is required for the JSON/aggregation cleanup.
3. The migration removes unsafe historical snapshot values while retaining allowed scalar metadata and original audit attribution/action/time. **Removed values cannot be recovered by rolling the migration back.** It does not fabricate historical business events.
4. Existing Owner roles receive `activity.view` once in the migration. Subsequent deliberate revocations are not undone on startup. New Owner roles receive the default; Manager does not.
5. Confirm access, two-session live delivery and the upgrade against your SQL Server staging database before production deployment. The sandbox browser used InMemory; transaction/durability verification used SQLite. The SQL Server upgrade was generated, not executed against a production database here.

The supplied provider settings remain in the complete solution as requested. **Keep this ZIP private.**

## Important boundaries

- No new asynchronous or partial-refund lifecycle was introduced. Legacy `Refunded` state remains; the feed and refund action messaging distinguish accepted requests from settlement.
- Each accepted payment refund is checkpointed before the next provider call. A provider success followed by a local persistence failure still requires reconciliation before retrying. This is not a distributed transaction or a guarantee against simultaneous external refund submissions.
- The existing review boolean cannot distinguish pending from rejected. Activity reports submission and actual publication changes, without inventing states.
- Live delivery is best-effort and at-least-once; persisted history is authoritative. Multi-instance deployments need the existing SignalR infrastructure configured for scale-out for prompt cross-node hints; polling/history remain the recovery path.

Technical details and source ownership are in [implementation notes](docs/business-activity/IMPLEMENTATION.md). The previously approved inspection and recommendations are preserved in [the decision report](docs/business-activity/APPROVED-DECISION.md).
