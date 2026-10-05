# Implementation and operational notes

## Reused infrastructure

`AuditLog` is extended with nullable business fields. Existing technical records and staff-report attribution remain intact. `AuditableSoftDeleteInterceptor` creates business projections and safe technical snapshots in the same EF SaveChanges operation. A failed relational save or outer transaction rollback therefore cannot leave a successful persisted activity for that mutation.

`BusinessActivityCapture` projects meaningful tracked scalar/owned-value differences, logical relationship-set changes and selected existing domain/timeline events. It merges the first business projection into the aggregate's existing technical audit row rather than creating a redundant parallel audit of that aggregate. Additional distinct business outcomes get distinct rows. Existing participant-facing timeline entries and notification workflows remain in place.

`DomainEvent` now supplies stable event IDs and occurrence times; repeatedly reading an event no longer generates a different identity. Payment outcomes use payment/outcome keys, existing child milestones use child IDs, and scalar changes use a save-batch identity. A unique filtered SourceKey index protects canonical sources. Previously generated, still-uncommitted interceptor audit rows are detached before a failed-save retry is rebuilt. Tests exercise an actual failed relational save and retry in the same context.

## Feed fields

Type, category, title, compact human summary, UTC occurrence time, safe actor ID/display identity/type, entity identity, optional order/customer/product/staff relationships, importance, source key, live eligibility and dispatch checkpoint. The read DTO contains no raw snapshots, IP addresses, emails, tokens, password hashes or gateway response bodies. Customer actions use a generic Customer identity; staff actions use the public staff username. Automatic assignment uses System attribution.

Known entity links are constructed by the Razor template and shown only for corresponding existing permission claims. Arbitrary entity-provided URLs are never rendered as audit links. Razor encodes summaries and labels.

## Activity policy

| Source | Projection | Delivery |
|---|---|---|
| Order | Creation, actual status changes, assigned/reassigned sales owner, finalized price | Live |
| Payment | Initialization; confirmed success/failure; accepted gateway refund/manual refund record | Initialization historical; outcomes live |
| Designs | Existing proof/review uploads and actual decisions | Live |
| Workspace | Discussion start, requirements confirmed, conversation resolved; attachment milestones | Operational milestones live; ordinary attachments historical |
| Inventory | Existing ledger movements; threshold crossing/shortage | Routine movement historical; shortage/threshold crossing live |
| Product | Creation/edit/delete, real price differences, category moves | Historical |
| Category/major category | Creation/edit/delete, association changes; logical ordering-option changes | Historical |
| Customer | Creation and supported profile/consent changes; contact values excluded | Historical |
| Review | Submission, deletion, actual publication-flag transitions | Historical |
| Staff/access | Staff creation/username; enabled/disabled/deleted accounts, role membership/permissions, role deletion | Access-affecting changes live; routine identity setup historical |

No per-message chat content, read/delivery receipts, typing/presence, waiting-state flips, login/token bookkeeping or notification read/update noise enters the business projection. Product and owned-money no-ops are suppressed. Category option replacement compares logical content rather than regenerated child IDs. Role/permission replacement compares ID sets; unchanged selections are silent.

The existing review model has only `IsApproved`; a distinct rejected/pending state is not inferred.

## Access and delivery

`ActivityAccess` queries the existing users/roles/permissions tables and active-account state. The feed requires authentication and this current permission check, not a stale cookie permission. The navigation entry uses the same current check. A newly delegated grant can work without a new login; revocation/disable blocks subsequent reads even with an old cookie.

`BusinessActivityDispatcher` uses a fresh database scope every two seconds and reads up to 100 committed eligible audit rows. It sends only `businessActivityChanged { refresh: true }` to currently authorized user groups on the existing NotificationHub. It creates no notification inbox records and runs outside the business save/transaction. The same audit rows store the dispatch timestamp; there is no separate bus/outbox table. Duplicate transport hints are harmless.

`business-activity.js` coalesces bursts, replaces server-rendered persisted results rather than appending duplicates, refreshes history on reconnect, and reconciles periodically at 15 seconds when visible. Background reads use a server-issued history cutoff to hold routine new history until refresh; page >1 does not auto-reorder. Revocation/session loss clears rows and stops the activity connection after the next authorized read. Server-side access denial is immediate; the open-page clearing interval is bounded by the reconciliation schedule under an active connection.

A tiny in-flight race between selecting recipients and permission revocation can deliver a content-free invalidation hint, never an activity payload. The subsequent fresh-authorized HTTP read denies content. Multi-node SignalR requires a deployment-appropriate backplane for prompt hints across nodes.

## Refund correctness

Refund eligibility is checked before external side effects. HTTP 2xx alone is insufficient for Paystack refund acceptance: the response must contain provider success and an object result, and explicit failed/rejected states are rejected. Malformed or unconfirmed responses do not create a refund success.

Each accepted payment is persisted before the next provider call. If a later payment fails, the earlier acceptance remains truthful and a retry skips it. Repeated charge callbacks/verification cannot change a refunded payment back to succeeded. A Paystack payment never falls into the manual-refund path simply because a gateway reference is missing; its payment reference is the fallback.

Legacy payment/order `Refunded` state is retained. The activity explicitly says **Gateway refund request accepted** with settlement unconfirmed, or **Manual refund recorded**, not terminal provider completion. Refund confirmation/toasts also explain this distinction and warn about reconciliation after unexpected failure. A provider call and the local database cannot be made atomic by this implementation; concurrent external submissions and provider-success/local-save-failure remain reconciliation boundaries, not claimed guarantees.

## Privacy migration

Migration `20260922040450_AddBusinessActivityAudit` adds the business fields/indexes, allowlists scalar JSON keys for known safe entity types in historical OldValues/NewValues, nulls unsafe/invalid/unsupported snapshots, and inserts/grants the new Owner permission once. Scalar JSON values keep their native JSON types; nested objects/arrays are not retained. Original action/entity/actor/time metadata remains. Historical technical rows have null ActivityType and never masquerade as reconstructed business events.

The cleanup is irreversible without a protected pre-upgrade backup. New forward snapshots use a positive allowlist; User, Customer, credentials, settings, raw messages and payment payloads are not copied. Existing generic PerformedBy attribution is retained for legacy reporting, but it is not exposed by the feed.

## Main source locations

- Domain: `Common/IDomainEvent.cs`, `Entities/AuditLog.cs`, existing domain event records.
- Persistence: `Interceptors/BusinessActivityCapture.cs`, `Interceptors/AuditableSoftDeleteInterceptor.cs`, `Queries/ActivityQueries.cs`, configuration and migration.
- Application: `Features/Activity/ActivityQueries.cs`; existing refund and payment commands.
- Host: `Controllers/Mvc/Admin/AdminActivityController.cs`, `Views/AdminActivity`, `wwwroot/js/business-activity.js`, `Realtime/ActivityAccess.cs`, `Realtime/BusinessActivityDispatcher.cs`.
- Tests: `BusinessActivityTests`, `BusinessActivityRelationalTests`, `PaystackRefundAcceptanceTests`; `e2e/activity/workflow.py` and `e2e/activity/persistence`.
