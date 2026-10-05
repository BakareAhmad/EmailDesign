# Wishlist verification — 23 September 2026

## Final results

| Check | Result |
|---|---|
| Complete solution build | Passed, zero errors; inherited warnings remain |
| Unit suite | 325 passed |
| Integration suite | 291 passed |
| Automated total | **616 passed, 0 failed, 0 skipped** |
| Wishlist browser workflow | **32 checks passed** |
| Business activity regression | **42 checks passed** |
| Category/ordering regression | **51 checks passed** |
| Design-review regression | **36 checks passed** |
| Browser total | **161 named checks passed** |
| Evidence | **12 ordered original screenshots**, screenshots only in `evidence/` |

Final browser journeys ran sequentially on one newly started InMemory sandbox host with tags `waudit1`, `wfinal1`, `wcat1`, `wreview1`. Only selected captures from this successful run were promoted. The final server log contained no unhandled exceptions or final HTTP 500 responses. Expected invalid requests produced the intended 400/404/authorization responses.

## Coverage

- Add controls on portal catalog and product details, outside navigation links; saved state and success messages.
- Filter/search/sort/page return preservation and local-only redirect handling.
- Repeated add and remove requests; remove/re-add; reload and a new authenticated session recover stored favourites.
- Different customers' lists remain isolated, including a forged remove targeting another customer's saved product.
- Existing public product saved state stays synchronized with portal actions; anonymous public visitors retain the sign-in-to-save prompt.
- Customer-only portal mutation, anti-forgery required, GET cannot mutate, unknown/inactive/deleted products rejected without a saved entry.
- A real stale browser page attempts to save a product deleted by the administrator and receives the visible 404 state.
- Wishlist → product → custom order request; saved plain product → cart with quantity 2; ordering does not remove independent favourites.
- Mobile label containment in both cards; keyboard activation; empty-state removal and return to portal shopping.
- Real SQLite unique constraint exercised across separate DbContexts: restore preserves the row ID and a repeated add leaves one physical row. This is not an InMemory-only uniqueness assertion.
- Existing activity permissions/live delivery/refunds, catalog hierarchy and ordering requirements, and live design review/validation/notification workflows all re-run successfully.

## Root causes addressed

1. Portal shop views never rendered the existing wishlist feature. Public product details did, which is why the earlier public-only wishlist tests passed.
2. Soft removal kept the unique customer/product row. A fresh insertion on re-add could violate that unique index. The repository now restores that row instead.
3. During testing, the context's new-child promotion checked physical existence through soft-delete filters and misclassified the restored row as new. It now ignores filters only for that physical-key existence check; normal customer/product reads remain filtered and scoped.
4. Mobile inspection found the new long remove label clipped in a two-column grid. Scoped button wrapping and an explicit browser containment assertion fixed it.

## Reproduce

With .NET 9, Python Playwright/Pillow and Chromium installed, from the repository root:

```sh
dotnet build DeChimexPrints/DeChimex.sln
dotnet test DeChimexPrints/DeChimex.sln --no-build
mkdir -p /home/user/e2e-dr-wip
python e2e/design-review/fixtures.py
```

Start `python e2e/design-review/brevo_stub.py` in one terminal. Start a fresh test app in another:

```sh
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://0.0.0.0:5119 \
Database__UseInMemory=true \
Brevo__ApiBaseUrl=http://127.0.0.1:9999 \
dotnet run --no-launch-profile --project DeChimexPrints/src/DeChimex.Host
```

Run the following in order, choosing unique tags and separate output directories:

```sh
E2E_SHOTS_DIR=/tmp/wishlist-check/activity python e2e/activity/workflow.py uniqueact
E2E_SHOTS_DIR=/tmp/wishlist-check/wishlist python e2e/wishlist/workflow.py uniquewish
E2E_SHOTS_DIR=/tmp/wishlist-check/major python e2e/major-categories/workflow.py uniquecat
E2E_SHOTS_DIR=/tmp/wishlist-check/review python e2e/design-review/ws_designreview_e2e.py uniquerev
```

Run the activity regression first: its duplicate-count assertions expect fresh activity data. The email stub captures verification tokens/headers locally; do not package or publish its capture file.

## Scope and limits

Browser testing used the app's InMemory preview option; persistence/unique-row restoration was separately checked against actual SQLite files. No production database was altered. Normal repeated requests are idempotent and the database still enforces uniqueness; simultaneous first-time submissions from different requests have not been exhaustively race-tested or serialized by this UI fix. No new migration or wishlist store was added.

The supplied keys remain unchanged. Test logs here contain summaries, not provider secret values. The prior activity verification documents are retained as clearly labelled historical records, not represented as new evidence.
