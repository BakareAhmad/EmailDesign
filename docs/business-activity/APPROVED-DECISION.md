# Admin Business Activity Audit — pre-implementation decision

**Date:** 22 September 2026  
**Baseline:** v047-DesignReview-Fixed-v4 with the completed Major Categories feature.  
**Status:** Source audit and proposed design only; awaiting agreement. No application changes or feature-completion claim yet.

## 1. Existing mechanisms to reuse

| Existing mechanism | Finding and proposed reuse |
|---|---|
| `AuditLog` / `AuditLogs` | Already persists action, entity, actor identifier, time and scalar before/after snapshots. Extend this table rather than adding a second activity store. |
| `AuditableSoftDeleteInterceptor` | Adds audit records to the same DbContext/SaveChanges transaction as aggregate changes. Reuse this atomic capture point, with business-specific classification and a strict field allowlist. |
| `UnitOfWork` + MediatR domain events | Existing order, assignment, design, payment-failure and registration events supply business intent. Reuse them; no new event bus and no controller-level duplicate logging. |
| `OrderTimelineEntry` | Existing per-order history contains submissions, status changes, assignments, designs, files, finalization and messages. Preserve it for order participants; use its event/transition sources, not a second copy of its prose. |
| `NotificationHub` | Existing SignalR transport supports permission groups and user groups. Use a dedicated activity-change signal, not extra notification/bell records. Never broadcast the audit feed through the general staff group. |
| Permission policies, roles and claims | Add a fine-grained `activity.view` permission within this system, granted to Owner initially and assignable to other authorized admin roles. Do not grant broad access merely because someone can sign in or view orders. |
| `/admin/reports/staff` | Currently counts generic audit actions by actor. Preserve its existing reporting purpose; it is not a detailed operational feed. |

## 2. Business activities and delivery policy

| Area | Activities worth recording | Delivery recommendation |
|---|---|---|
| Orders | Creation/submission, genuine status transitions including cancellation/completion, assignment/reassignment, final pricing changes, supported workflow transitions | Real-time |
| Payments | Initialization, persisted verification failure, first successful confirmation, payment-related order transition | Success/failure and order transition real-time; initialization persisted-only |
| Refunds | Confirmed local/manual refund recording; accepted gateway requests; verified provider completion only when actually evidenced | Operational refund outcomes real-time, with truthful wording |
| Products | Creation, meaningful edits, price/category changes, active/archive changes | Persisted-only |
| Inventory | Stock adjustment/receipt/removal; shortage/low-stock attention events | Routine stock movements persisted-only; shortage/low-stock alerts real-time |
| Categories | Major/subcategory creation, edits, deletion and reassignment | Persisted-only |
| Customers | Account/profile creation and meaningful profile changes, without contact-field values | Persisted-only; customer orders appear through the order activities |
| Staff/access | Account creation, enable/disable, username changes, role assignment and permission changes | Access-affecting changes real-time; routine account creation/username edits persisted-only |
| Designs | Proof/version upload, approval/change request, independent design-review upload and approval/rejection | Real-time |
| Reviews | Submission and actual approval/publication-status changes | Persisted-only |
| Communication | Conversation first started, explicit resolution or other supported important state changes, dedicated order-file attachments | Real-time for milestones; attachment records persisted-only unless part of a design approval workflow |

**Exclude:** individual chat messages, typing, presence, read receipts, ordinary waiting-for-customer/staff flips after each message, notification creation/read changes, login bookkeeping, token refresh/reset values, and no-op saves.

A price/category change during one product edit should be one business activity with compact safe change details—not a generic update plus several duplicate copies. Multiple genuine events, such as an order being created and assigned, remain distinct. Re-verification of an already-paid payment must not create another payment-success activity.

## 3. Recommended persistence and transaction design

Extend `AuditLogs` with nullable, indexed business fields: activity type/category, human-readable title/summary, actor user ID and safe display identity/role, related order/customer/product/staff IDs where relevant, importance, real-time eligibility, and a stable source/deduplication key. Keep existing action/entity/time fields and legacy reporting compatible.

The new admin feed reads **only explicitly classified business activities**. It never displays or returns raw `OldValues`, `NewValues`, IP addresses, tokens, gateway payloads, chat/review text, customer addresses or arbitrary entity serialization. Monetary/status changes and entity identifiers are sufficient metadata in most cases.

Use **one central projection at the existing persistence boundary**:

- Existing domain events provide semantic intent for orders, assignments and designs.
- Actual original/current values provide catalog, customer, payment and account changes where no appropriate event exists. Include EF owned values (prices) and relationship collections (roles/permissions); a root-only scalar scan would miss them.
- Define one authoritative source for each activity type. Do not independently log the same action from domain events, timeline entries, notification handlers and controllers.
- Enrich the existing audit row when it corresponds to one business activity; support distinct business records where an operation genuinely contains several activities. Preserve non-business technical history separately within the same table, excluded from the business feed.
- Persist activities in the **same SaveChanges transaction** as the state they describe. Validation errors or rolled-back saves create no successful activity. Retry bookkeeping must not accumulate duplicate pending audit rows.
- Signal clients only after successful commit. Handle explicit transaction boundaries if encountered; do not publish during `SavingChanges`. A delivery failure must not undo a committed business operation.
- Give emitted domain events stable identity/time before relying on them for deduplication. The current interface defaults return `Guid.NewGuid()` / `UtcNow` on every property read; they are not stable occurrence identifiers.
- Suppress no-op field/relationship changes and repeated payment callbacks. Add a database uniqueness guard for activity source keys as a final protection.

### External-payment boundary

Payment confirmation currently spans multiple saves: payment success is persisted before downstream order/receipt processing. The audit must describe each **committed outcome**, not pretend the entire HTTP request is one atomic transaction.

The refund coordinator calls the provider before saving local state. A remote refund cannot be rolled back by a database transaction; multi-payment refunds and a local save failure can therefore need reconciliation. Do not report full completion merely because a request returned 2xx or the local enum became `Refunded`.

**Proposed scope:** record truthful outcomes supported by the current workflow—such as “Gateway refund request accepted” or “Manual refund recorded”—and test rejection/failure paths. Do not invent partial-refund completion: the current workflow refunds whole succeeded payment amounts and has no complete partial/asynchronous refund lifecycle. Adding that lifecycle would be a separate payment feature unless explicitly requested here.

## 4. Admin UI, filtering and reconnects

Create **Admin → Business activity**, with a readable chronological feed:

- Title and concise description; safe actor identity; category/importance badge.
- Relative time with an exact timestamp available.
- Links to related entities only where the viewer has the relevant access; target controllers retain their own authorization.
- Filters for category/activity type, date range, actor, order, customer, product, staff and importance. Server-side bounded search over safe summaries/reference identifiers only.
- Bounded page sizes, indexed filtering and deterministic timestamp/ID pagination. Do not load the entire audit table into memory.

For real-time-eligible events, send a small **invalidation signal**, then load persisted rows through the authorized feed endpoint. This reuses SignalR without distributing sensitive audit payloads to stale connections. Respect the current filters; coalesce bursts rather than adding a toast for every row.

On initial load, reconnect, returning to the page, and a modest fallback interval, reconcile with persisted history. Do not depend solely on a last-received SignalR event or assume pre-commit timestamps are commit-ordered. History remains pageable after long outages; users do not lose activities just because they were disconnected.

Recheck current enabled-account and permission state for feed access so revoked access cannot continue reading through an old cookie/connection. Test two authorized admin sessions and denied customer/staff sessions.

## 5. Overlap with order history

The order timeline remains the participant-facing history for one order. The admin feed is a privacy-filtered cross-system operational projection of the **same underlying events and committed changes**.

Do not republish every timeline row or chat message, and do not replace the order timeline. Existing notifications retain recipient-specific read/unread behavior; audit entries are durable business facts, not another notification inbox.

## 6. Risks and safeguards found in the audit

1. **Sensitive legacy snapshots:** the current interceptor only excludes audit bookkeeping, not password hashes, verification/reset/share tokens, application-setting values or payment payloads. Forward capture needs an explicit allowlist. Recommend a controlled cleanup of unsafe fields in existing audit snapshots, retaining action, entity, actor/time and safe history. Never infer trustworthy business history from raw legacy snapshots or expose them in the new UI.
2. **Post-save event timing:** adding only a new MediatR notification handler would leave a gap between business commit and activity persistence. Capture the business projection in the original save instead.
3. **Nested saves:** existing notification handlers call UnitOfWork again. Exclude notification bookkeeping and ensure already-captured events are not replayed into new activity rows.
4. **Collection/owned-value changes:** roles/permissions and prices require more than root scalar tracking. Compare logical sets/values; `IsModified` alone does not prove a meaningful change.
5. **Refund semantics:** provider acceptance is not terminal settlement; see the scope above.
6. **Review semantics:** `Review.IsApproved` is currently a boolean. Pending and rejected are both false. Audit actual publication changes without claiming a richer moderation state that is not persisted.
7. **Growth/noise:** filter aggressively, index useful dimensions, bound queries and batch real-time invalidations. Do not introduce destructive automatic retention in this feature; document future retention needs.
8. **Historical precision:** no fabricated retroactive business events. New typed history begins when the feature is deployed; existing order timelines and legacy staff reports remain available.

## 7. Verification and delivery plan

- Unit/integration tests for classifications, safe payloads, owned/collection diffs, no-op suppression, stable IDs, duplicate callbacks and retry behavior.
- Real relational tests for commit/rollback atomicity, unique source keys, and persistence across database connections/process restart—not only InMemory refresh tests.
- Actual browser actions generate order/payment/design/product/category/staff events while two admin feeds remain open. Verify immediate delivery only for qualifying types, historical-only events on refresh, filtering and navigation.
- Disconnect/reconnect one session, create activities while it is offline, then verify persisted recovery without duplicates. Test permission removal and disabled accounts.
- Exercise failed validation, failed writes, payment failure and refund rejection/acceptance without false completion labels. Use deterministic gateway/email test doubles, not paid live transactions.
- Confirm ordinary chat traffic and notification bookkeeping do not flood the feed; rerun connected workflows and the full suite.
- Perform one clean final E2E run, retain only its ordered screenshots in `evidence/`, update the handoff and commit.
- Package the **entire updated solution**, migration, tests, documentation and final evidence. Retain the supplied appsettings keys as instructed; keep the ZIP private.

## Decisions requested

1. Approve extending the existing audit table/persistence pipeline and SignalR infrastructure, with `activity.view` and the real-time policy above?
2. Approve sanitizing unsafe fields from historical audit snapshots while retaining their safe metadata/history? Alternatively, preserve historical snapshots unchanged but keep them entirely excluded from the new feed and harden new writes.
