# DE-CHIMEX PRINT HUB — Custom Order Flow & Category-Driven Ordering
## DESIGN & IMPLEMENTATION PLAN (v1 — for owner approval)

> Prepared after a full inspection of the codebase at origin `v047-EmailQuotaFix`
> (commit `71ff542` + the 2026-09-09 key insertion). This plan maps every section of
> the owner's 27-section specification onto the existing architecture. Where the spec
> overlaps with an existing feature, we **reuse** it; new code is created only where
> there is a genuine gap. Nothing here duplicates an existing system.

---

## 1. Confirmed decisions (owner's answers, 2026-09-09)

| Decision | Choice | Consequence |
|---|---|---|
| Sequencing | **Plan first, then build** | This document; implementation begins after approval |
| Simple vs custom routing | **Auto-detect** | Category has active options **OR** product `IsCustomizable` → Order Request + staff review; otherwise → direct checkout |
| Cart | **Keep for simple products** | Plain products keep "Add to cart" → checkout; products whose category has options (or are customizable) get an "Order / Customize" CTA |
| Chat | **Real-time (SignalR)** | Live, WhatsApp-like order chat on the existing SignalR foundation |

---

## 2. Guiding principle ("one coherent order lifecycle")

The spec (§24/§25) forbids two competing order flows. The current code already has a
single `Order` aggregate with a state machine, one payment pipeline, one invoice
system and one set of read models. We therefore:

- **Extend the existing `Order` aggregate** with pre-payment ("order request") statuses
  and new child collections (responses, files, messages, timeline, proofs, custom lines).
- Do **NOT** introduce a separate `OrderRequest` table/aggregate. The "Order Request" is
  the same `Order` in its pre-finalization states. The "final order" is the same `Order`
  once staff finalize pricing and it moves to `PendingPayment`. One order number
  (`DCX-…`) from submission to delivery, matching the spec's examples.

Rationale (documented for future sessions): a second order system would fork payments,
invoices, references, permissions and notifications — exactly the duplication §24 bans.
The `Order` table gains a nullable `Status`-agnostic discriminator of nothing; the new
statuses alone carry the semantics.

---

## 3. REUSE vs NEW (explicit map)

| Requirement (spec §) | Reused existing | New |
|---|---|---|
| Category = product type | `Category` (rename nothing; add options) | `CategoryOption`, `CategoryOptionChoice` |
| Product catalog | `Product` (price, SKU, images, `IsCustomizable`, category) | — |
| Order lifecycle | `Order` state machine | new statuses + transition methods |
| Order lines | `OrderItem`, `OrderServiceLine` | `OrderCustomLine` (ad-hoc charge lines at finalization) |
| Payment | `InitializePayment`/`VerifyPayment`/`HandleWebhook` + `PaymentConfirmationService` (already server-verified, idempotent) | — |
| Invoices/receipts | `Invoice.Issue`, quote-invoice path, `Receipt` | invoice issued at **finalization** (was at placement for cart orders; unchanged for cart path) |
| Files | `IFileStorageService`/`LocalFileStorageService` | `OrderFile`, `OrderDesignProof` |
| Chat | `ContactMessageReply` pattern | `OrderMessage` + `OrderChatHub` (SignalR) |
| Timeline | — (AuditLog is staff-internal, not customer-facing) | `OrderTimelineEntry` |
| Notifications | `INotificationService`, `Notification`, `NotificationPusher`, `NotificationHub` | new `NotificationType` values |
| Email | `IEmailQueue`, `BrevoEmailService`, `EmailTemplateRenderer`, `IAppUrlProvider` | new `EmailTemplate` members + renderer cases + handlers |
| Permissions | `Permissions.Orders.*` (reused; no new permissions needed) | — |
| Customer portal/storefront/admin layouts | `_PublicLayout`, `_PortalLayout`, `_AdminLayout` | new views + one nav link |

---

## 4. DATA MODEL (new entities — all additive)

### 4.1 `CategoryOption` (child of `Category`)
Staff-configured ordering question reusable by every product in the category (§2, §19, §20).

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| CategoryId | Guid (FK) | |
| Question | string (≤300) | e.g. "Do you want customization?" |
| Type | `CategoryOptionType` | `SingleChoice=0, MultipleChoice=1, Text=2, LongText=3, FileUpload=4` |
| IsRequired | bool | |
| DisplayOrder | int | |
| ParentOptionId | Guid? (self-FK, Restrict) | conditional visibility |
| ShowWhenValue | string? | value on the parent that reveals this option |
| IsActive | bool | soft-hide without deleting history |

Rules:
- `SingleChoice`/`MultipleChoice` require ≥1 choice.
- `FileUpload`/`Text`/`LongText` have no choices.
- Conditional visibility: option shows when its parent's answer **equals** `ShowWhenValue`
  (single/text/file) or **contains** it (multiple). Covers the spec examples:
  "Customization = No → hide 'what to customize'", "Existing design = Yes → show 'upload design'".
- Responses snapshot `Question`/`Type`, so re-editing a category's options never corrupts
  historical orders (§2: "future orders use the current configuration").

### 4.2 `CategoryOptionChoice`
| Field | Type |
|---|---|
| Id | Guid |
| CategoryOptionId | Guid (FK, cascade) |
| Label | string (≤200) |
| DisplayOrder | int |

### 4.3 `OrderRequirementResponse` (child of `Order`)
One row per answered option on a submitted order request (§5).
| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| OrderId | Guid (FK) | |
| CategoryOptionId | Guid? | reference only (option may be edited/deleted later) |
| Question | string | snapshot |
| Type | `CategoryOptionType` | snapshot |
| Value | string | JSON array for single/multi; plain string for text/longtext; file reference handled by `OrderFile` (linked via `FileToken`) |
| DisplayOrder | int | |

File-upload answers create an `OrderFile` row; the response stores the file id(s) as JSON.

### 4.4 `OrderFile` (child of `Order`) — general attachments (§10)
| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| OrderId | Guid (FK) | |
| StoredFileName | string | storage key |
| OriginalFileName | string | |
| Url | string | served path |
| ContentType | string | |
| SizeBytes | long | |
| Purpose | string? | "design", "reference", "logo", "proof", … |
| UploadedByUserId | Guid? | |
| UploadedByRole | string | "customer" / "staff" |
| CreatedDateUtc | DateTime | |

Never silently deleted; explicit delete only (§10).

### 4.5 `OrderDesignProof` (child of `Order`) — versioned proofs (§11)
| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| OrderId | Guid (FK) | |
| StoredFileName / OriginalFileName / Url / ContentType | | as OrderFile |
| Version | int | monotonically increasing per order |
| StaffNotes | string? | |
| Status | `DesignProofStatus` | `PendingApproval=0, Approved=1, ChangesRequested=2, Superseded=3` |
| CustomerFeedback | string? | set on ChangesRequested |
| ApprovedAtUtc / ApprovedByUserId | DateTime? / Guid? | "the version approved for production" is the row with Status=Approved (latest) |

Rules:
- Uploading a new proof → previous `PendingApproval`/`Approved` rows become `Superseded`.
- Customer `Approve` → Status=Approved (+timestamp). `RequestChanges(feedback)` →
  ChangesRequested (+feedback); order returns to `InDiscussion`.
- Only the **Approved** version is identifiable as production-ready.

### 4.6 `OrderMessage` (child of `Order`) — order-specific chat (§7)
| Field | Type |
|---|---|
| Id | Guid |
| OrderId | Guid (FK) |
| AuthorUserId | Guid? |
| AuthorRole | string ("customer"/"staff") |
| AuthorName | string (denormalized for display) |
| Body | string (≤4000) |
| CreatedDateUtc | DateTime |

Server-authoritative: messages are persisted by a handler, then broadcast via SignalR.

### 4.7 `OrderTimelineEntry` (child of `Order`) — history (§17)
| Field | Type |
|---|---|
| Id | Guid |
| OrderId | Guid (FK) |
| Type | string (e.g. "created", "file", "message", "proof", "approved", "finalized", "payment", "status") |
| Description | string |
| ActorUserId | Guid? |
| ActorName | string |
| CreatedDateUtc | DateTime |

Written by handlers for every significant action (submission, uploads, messages, proofs,
approval, finalization, payment, status changes).

### 4.8 `OrderCustomLine` (child of `Order`) — ad-hoc charge lines (§12)
| Field | Type |
|---|---|
| Id | Guid |
| OrderId | Guid (FK) |
| Description | string (≤250) |
| UnitPrice | decimal |
| Quantity | int |

Used for "Customization × 10", "Design preparation", etc. at finalization. Included in
`ItemsSubtotal`/`GrandTotal`.

### 4.9 Changes to existing `Order` aggregate
- New child collections: `Responses`, `Files`, `Messages`, `Timeline`, `Proofs`, `CustomLines`.
- New domain methods:
  - `SubmitOrderRequest(...)` — creates order in `AwaitingReview` (no invoice yet).
  - `AddResponse(...)`, `AddFile(...)`, `AddMessage(...)`, `AddTimeline(...)`,
    `AddProof(...)`, `ApproveProof(...)`, `RequestProofChanges(...)`,
    `SetCustomLines(...)`, `UpdateLinePricing(productId, qty, unitPrice)` (mutable only
    pre-payment — existing `EnsureMutable()` guard extended).
  - Status transitions: `StartDiscussion()`, `ConfirmRequirements()`, `AwaitDesignApproval()`,
    `ReopenDiscussion()`, `Finalize()` (→ `PendingPayment`), `QualityCheck()`, `Dispatch()`
    (→ `OutForDelivery`), plus existing `MarkPaid/StartProduction/MarkReady/…`.

---

## 5. STATUS MACHINE (§14, §15)

### 5.1 Internal statuses (extended `OrderStatus` — **additive only, values 10+**)
Existing values 0–9 are untouched (never renumber; legacy rows must keep meaning).

| Value | Status | Meaning | Moved by |
|---|---|---|---|
| 0–9 | (existing) | Draft…Refunded | unchanged |
| 10 | **AwaitingReview** | Order Request submitted; staff must look | system (customer submit) |
| 11 | **InDiscussion** | staff/customer clarifying via chat + files | staff |
| 12 | **RequirementsConfirmed** | staff has what's needed; ready to finalize | staff |
| 13 | **AwaitingDesignApproval** | proof uploaded; awaiting customer approve/changes | staff (upload proof) |
| 14 | **QualityCheck** | production done, QC gate | staff |
| 15 | **OutForDelivery** | dispatched | staff |

> "Queued for Production" from the spec is represented by `Paid` (paid, awaiting
> production start) — no extra status is added, to avoid over-engineering. "Order
> Request" + "Awaiting Staff Review" from the spec are merged into `AwaitingReview`.

### 5.2 Transitions (guarded in the domain, mirrors the existing pattern)
```
Complex/custom path:
 AwaitingReview ──staff──▶ InDiscussion ◀──(request changes)── AwaitingDesignApproval
 InDiscussion ──staff──▶ RequirementsConfirmed ──staff Finalize()──▶ PendingPayment ──Pay──▶ Paid
 AwaitingDesignApproval ──customer approve──▶ InDiscussion (staff then RequirementsConfirmed)

Simple path (unchanged):
 cart → Draft ──PlaceOrder()──▶ PendingPayment ──Pay──▶ Paid

Production (shared):
 Paid ──StartProduction──▶ InProduction ──MarkReady──▶ Ready ──Dispatch──▶ OutForDelivery ──MarkDelivered──▶ Delivered
                             │
                             └──QualityCheck()──▶ QualityCheck ──MarkReady──▶ Ready
 Cancel allowed from AwaitingReview/InDiscussion/RequirementsConfirmed/PendingPayment/Draft (existing Cancel()).
```
- `Finalize()` requires `RequirementsConfirmed` (custom) or `Draft` (staff counter path),
  sets pricing, issues the invoice, and transitions to `PendingPayment`.
- `MarkPaid()` (existing) is unchanged and already requires `PendingPayment`/`Draft`.

### 5.3 Customer-facing projection (§15) — displayed, never stored
| Internal | Customer sees |
|---|---|
| AwaitingReview | **Order Received** |
| InDiscussion / RequirementsConfirmed | **Being Reviewed** |
| AwaitingDesignApproval | **Awaiting Your Approval** (with an "Action Required" callout) |
| PendingPayment | **Payment Required** |
| Paid / InProduction / QualityCheck | **In Production** |
| Ready | **Ready** |
| OutForDelivery | **Out for Delivery** |
| Completed / Delivered | **Completed** |
| Cancelled / Refunded | **Cancelled / Refunded** |

A single `CustomerStatusFor(OrderStatus)` helper (Shared or Domain) is the source of truth.

---

## 6. AUTO-ROUTING RULE (§22)

On `/products/{id}` (storefront) and `/portal/shop` cards:
- If **category has any active `CategoryOption`** OR **`Product.IsCustomizable`** →
  primary CTA **"Order / Customize"** → `/products/{id}/order` (requirement form).
- Else → **"Add to cart"** (existing, unchanged).

After the requirement form is submitted:
- A category with options or a customizable product **always** becomes an `Order`
  in `AwaitingReview` (staff review). The customer lands in the Order Workspace.
- (The customer never manually picks a workflow — the rule above decides, per §22.)

---

## 7. CATEGORY OPTION CONFIGURATION (§19) — admin UX

- `/admin/categories/{id}/options` — a dynamic form (list of option rows, each with
  question, type, required, choices, and an optional "show when" parent+value picker).
- Save = **replace the whole set** for the category (delete + re-add) — simplest correct
  semantics. Historical responses are safe because they snapshot question/type/value.
- Conditional references are validated server-side (parent must exist in the same set,
  parent must be a choice type, no cycles).
- The customer-facing form (`/products/{id}/order`) renders options with client-side
  show/hide on parent answer changes (§4: conditional questions only appear when relevant).

Option type → rendered control:
| Type | Control |
|---|---|
| SingleChoice | radio group |
| MultipleChoice | checkboxes |
| Text | short input |
| LongText | textarea |
| FileUpload | file input (accepts images/pdf) |

---

## 8. APPLICATION LAYER (CQRS)

New commands/handlers (in `Features/OrderRequests/`, `Features/Orders/`, `Features/Categories/`):
1. `GetCategoryOptionsQuery` — options + choices for a category.
2. `SaveCategoryOptionsCommand` — replace the set (staff).
3. `SubmitOrderRequestCommand` — product + quantity + responses + uploaded files →
   `Order.AwaitingReview` + responses/files/timeline + staff notification
   (`NotificationType.OrderRequestReceived`) + email to customer.
4. `GetOrderWorkspaceQuery` — full workspace read model (order, responses, files,
   messages, timeline, proofs, payment, invoice).
5. `SendOrderMessageCommand` — persist message + timeline + notify the other party
   (+ SignalR push via `IOrderChatPusher`).
6. `UploadOrderFileCommand` — customer or staff upload → `OrderFile` + timeline (+ optional
   "information requested" notification).
7. `DeleteOrderFileCommand` — explicit delete + timeline.
8. `UploadDesignProofCommand` (staff) — versioned proof → `AwaitingDesignApproval` +
   customer notification/email.
9. `ApproveDesignProofCommand` (customer) — approve → timeline + staff notification.
10. `RequestDesignChangesCommand` (customer) — feedback → `InDiscussion` + staff notification.
11. `FinalizeOrderCommand` (staff) — set line pricing + custom lines + delivery/tax/discount
    → `RequirementsConfirmed` → `PendingPayment` + issue `Invoice` + notify customer
    ("Your order is ready for review").
12. `SetOrderRequirementsStatusCommand` — staff transitions among
    AwaitingReview/InDiscussion/RequirementsConfirmed (guarded).
13. Extend `UpdateOrderStatusHandler` — add `QualityCheck`, `OutForDelivery`, `Dispatch`
    transitions (staff), reusing the existing switch.

New domain events + handlers (mirroring `OrderEventHandlers`):
`OrderRequestSubmittedEvent`, `OrderMessageAddedEvent`, `OrderFileAddedEvent`,
`DesignProofUploadedEvent`, `DesignProofApprovedEvent`, `OrderFinalizedEvent`,
plus status emails via the existing `OrderStatusChangedEvent` (extended filter).

Read side: `IOrderWorkspaceQueries` (or extend `IOrderQueries`) with
`GetWorkspaceAsync(orderId)` returning a DTO grouping all the above.

New DTOs in `Contracts/Orders`: `OrderWorkspaceDto`, `OrderRequirementResponseDto`,
`OrderFileDto`, `OrderMessageDto`, `OrderTimelineEntryDto`, `OrderDesignProofDto`,
`OrderCustomLineDto`, `CategoryOptionDto`, `CategoryOptionChoiceDto`,
`SubmitOrderRequest`, `FinalizeOrderRequest`, etc.

---

## 9. PERSISTENCE

- `DeChimexDbContext`: add `DbSet`s for the 7 new entities; `ApplyConfigurationsFromAssembly`
  already picks up new `IEntityTypeConfiguration` classes.
- New config classes in `Configurations/` (follow existing conventions; self-FK on
  `CategoryOption.ParentOptionId` uses **`OnDelete(Restrict)`** — the SQL Server
  self-referencing-cascade pitfall from §32 of AI_HANDOFF).
- **One new migration** (`AddCustomOrderFlow`) — additive only, on top of the 13 existing.
  Never edit a released migration. Enum values are stored as strings/int as today, so
  adding enum members needs no migration; the migration covers the new tables only.
- Repositories: `IOrderRepository.GetWithWorkspaceAsync(...)` (Include responses/files/
  messages/timeline/proofs/customLines), `ICategoryRepository.GetWithOptionsAsync(...)`,
  and a `IOrderWorkspaceRepository`/`IOrderRequestRepository` where a dedicated repo is
  cleaner. Registered in `Persistence/DependencyInjection.cs`.

---

## 10. HOST LAYER

### 10.1 Routes
| Route | Purpose | Auth |
|---|---|---|
| `GET /products/{id}/order` | requirement form (dynamic options) | Customer (or anon → login redirect) |
| `POST /products/{id}/order` | submit Order Request | Customer |
| `GET /portal/orders/{id}/workspace` | customer workspace (details/files/chat/timeline/pricing) | Customer (owner of order) |
| `POST /portal/orders/{id}/messages` | send chat message | Customer (owner) |
| `POST /portal/orders/{id}/files` | upload file | Customer (owner) |
| `POST /portal/orders/{id}/proofs/{proofId}/approve` / `…/request-changes` | design decisions | Customer (owner) |
| `POST /checkout/{id}/pay` (existing) | pay finalized order | Customer (owner) |
| `GET /admin/orders/{id}/workspace` | staff workspace (full) | `orders.view` |
| `POST /admin/orders/{id}/messages` / `…/files` / `…/proofs` / `…/finalize` / `…/status` | staff actions | `orders.view` / `orders.updatestatus` |
| `GET/POST /admin/categories/{id}/options` | configure options | `categories.manage` |
| `HUB /hubs/orderchat` | SignalR chat | Authorize (customer owner or staff) |

- Storefront CTA change in `Views/StoreProducts/Details.cshtml` + `Views/PortalShop/Index.cshtml`
  (a small `@if` on `HasCategoryOptions || IsCustomizable`).
- Workspace views: new `Views/Store/OrderRequest/Index.cshtml` (the form),
  `Views/PortalOrders/Workspace.cshtml` (customer), `Views/AdminOrders/Workspace.cshtml`
  (staff, or a partial embedded in the existing Details page), `Views/AdminCategories/Options.cshtml`.
- Nav: one "Orders" link already exists in portal + admin; the workspace is reached from the
  existing order lists (add a "View workspace" affordance on order rows/details).

### 10.2 Real-time chat (§7) — `OrderChatHub`
- New hub `OrderChatHub` at `/hubs/orderchat`, `[Authorize]`.
- On connect, client calls `JoinOrder(orderId)`; server verifies the caller is the order's
  customer **or** holds `orders.view`, then adds the connection to group `order:{orderId}`.
- `SendMessage(orderId, body)` persists via `SendOrderMessageCommand` (server-authoritative)
  and the handler broadcasts `"orderMessage"` to `order:{orderId}` via
  `IOrderChatPusher` (mirrors `NotificationPusher`).
- Client JS: reuse the vendored `wwwroot/lib/signalr` (already shipped). Workspace views
  connect, render the thread, and fall back to a normal page refresh if the hub is unavailable.
- The same "new message" flow also writes an in-app `Notification` (type
  `OrderMessageReceived`) so the other party is alerted even when offline.

---

## 11. NOTIFICATIONS & EMAILS (§16)

New `NotificationType` members (stored as string → no migration):
`OrderRequestReceived`, `OrderMessageReceived`, `OrderInfoRequested`, `OrderQuoteReady`,
`DesignProofReady`, `DesignProofApproved`, `DesignProofChangesRequested`, `OrderReadyForPickup`, `OrderOutForDelivery`.

New `EmailTemplate` members + `EmailTemplateRenderer` cases:
`OrderRequestReceived`, `OrderMessageNotification`, `OrderQuoteReady`, `DesignProofReady`,
`DesignProofApproved`, `OrderOutForDelivery` (reusing `OrderStatusUpdate` for
production milestones).

Handlers wire the domain events above to `INotificationService.NotifyAsync` +
`IEmailQueue.EnqueueAsync` exactly like the existing `OrderEventHandlers`
(fire-and-forget; a mail outage never blocks the order flow). Guest customers
(no email) are skipped, as today.

---

## 12. PERMISSIONS (§9)

No new permission strings. Reuse:
- `orders.view` → staff workspace read, list.
- `orders.updatestatus` → finalize, status transitions, proofs upload.
- `categories.manage` → configure category options.
- `StaffCounterPolicy` remains for counter/quote orders.
Customer access is ownership-based (the workspace actions check
`order.CustomerId == current customer`), matching the existing `CheckoutController`
pattern.

---

## 13. GUARDRAILS (from AI_HANDOFF — must not break)

1. **Migrations: additive only** on top of the 13 existing; never edit released migrations;
   keep the model snapshot in sync.
2. **Invoice before payment**: for the custom flow the invoice is issued at `Finalize()`
   (before `PendingPayment`); receipt after payment. The existing cart path is unchanged.
3. **Inventory after payment only**: `SubmitOrderRequest`/`Finalize` never touch stock;
   stock reserves on payment and deducts at `StartProduction` (existing
   `PaymentConfirmationService` / `OrderFulfilmentService` / `UpdateOrderStatusHandler`).
4. **Payment verification is server-side** (reuse `VerifyPayment`/`HandleWebhook`): never
   mark paid on a redirect alone; the idempotent pipeline stays.
5. **No pay-on-delivery**; enum value-2 gap preserved.
6. **Cookies-only auth**; no new JWT surface; transport-agnostic `PermissionPolicyProvider`
   unchanged.
7. **Don't break the simple cart/checkout path** — the new CTA is conditional and additive.
8. **Never print secrets**; appsettings keys already handled per §66.
9. **`AI_HANDOFF.txt` updated** after implementation (per its mandatory contract).

---

## 14. TESTING PLAN

- **Domain/unit**: `CategoryOption` validation + conditional resolution; `Order` new
  transitions (legal/illegal per §5.2); `DesignProof` version/supersede/approve;
  `Finalize` pricing incl. custom lines; `CustomerStatusFor` projection.
- **Integration** (existing `CustomWebApplicationFactory` + InMemory): 
  - category options CRUD via admin surface;
  - submit Order Request (required/conditional/file) → `AwaitingReview` + responses;
  - workspace authorization (owner vs other customer vs anon vs staff);
  - chat: send as customer/staff → persisted + other party blocked from other orders;
  - proof approve / request-changes loop (multiple versions);
  - finalize → invoice issued → `PendingPayment` → pay (fake gateway) → `Paid`;
  - production transitions incl. `QualityCheck`/`OutForDelivery`;
  - notifications/email side-effects (fakes), timeline entries written.
- Regression: full existing suite must stay green (was 382/382 at §65; new tests add to it).
- Live smoke: boot InMemory, walk the whole loop end-to-end (Playwright where useful).

---

## 15. IMPLEMENTATION ORDER (phases)

1. **Foundation**: enums (`CategoryOptionType`, `DesignProofStatus`, new `OrderStatus`,
   `NotificationType`, `EmailTemplate`) + `CustomerStatusFor` helper.
2. **Domain**: new entities + `Order` extensions + domain events.
3. **Persistence**: DbContext DbSets + configurations + `Order`/`Category` repository
   extensions + migration (verify `has-pending-model-changes` clean).
4. **Application**: category-option commands/queries; submit/workspace/chat/file/proof/
   finalize commands + validators + event handlers; read-side DTOs + workspace queries.
5. **Host**: controllers + routes + views (order form, workspaces, admin options);
   CTA switch in storefront/portal shop; `OrderChatHub` + pusher + client JS.
6. **Notifications/emails**: template renderer cases + handler wiring.
7. **Tests** (all layers) + full build + `dotnet test` + live boot + smoke.
8. **Docs**: update `AI_HANDOFF.txt` (new section), `CHANGELOG.md`, and package a new
   deliverable zip (keys included per §66/§31 directive).

---

## 16. OPEN QUESTIONS / RISKS (no owner action needed unless noted)

- **Ad-hoc charge lines** are modeled as `OrderCustomLine` (not reused `OrderServiceLine`),
  because `OrderServiceLine.ServiceId` is catalog-bound. Confirmed during build.
- **Email subject/body wording** for the new templates will follow the existing tone;
  owner can tweak copy after first build.
- **File upload size/type limits** reuse the existing `Storage` options
  (`MaxFileSizeBytes`, allowed extensions); design files may be images/PDF — confirmed
  against `AllowedDocumentExtensions`.
- The plan touches **no** existing payment/invoice/inventory rules; the cart flow is
  byte-for-byte intact.

---

## 17. SALES ASSIGNMENT EXTENSION (implemented 2026-09-11 — see AI_HANDOFF §78)

Follow-on to plan v1; all constraints from §13 upheld (no payment/invoice/
inventory/chat/permission/cart changes; `Order` remains the single aggregate).

- **Ownership**: `Order.AssignedSalesUserId/AssignedSalesName` + `ConversationState`
  (`Active/WaitingForCustomer/WaitingForStaff/Resolved`), purely additive
  migration `20260911012805_AddSalesAssignment`.
- **Auto-assign**: new `AwaitingReview` custom orders go to the eligible sales
  staffer with the lowest active workload (active =
  `AwaitingReview/InDiscussion/RequirementsConfirmed/AwaitingDesignApproval`);
  ties break by earliest-created account, then email. No staff → unassigned
  triage bucket.
- **Conversation**: sender-role flips (`WaitingForStaff`/`WaitingForCustomer`),
  resolved on completion/cancel/refund with history retained; `OrderStatus`
  never reflects chat state.
- **Manual reassign**: owner/admin (`users.assignroles`), idempotent, timeline
  history with names on staff views, new-owner notification.
- **Sales inbox** (`/admin/sales`): `new/active/waiting-customer/waiting-staff/
  resolved/unassigned`; staff see own queue, leads see all + filter.
- **Customer privacy**: portal gets `RedactForCustomer()` — generic handled-by
  note + generic assignment timeline text; staff names never render.
- **Verification**: 451/451 suite (45 new tests), 44-check sandbox E2E green,
  evidence in `DeChimexPrints/TestArtifacts/SalesAssignmentFinal/`.

*End of plan v1 + sales-assignment extension.*
