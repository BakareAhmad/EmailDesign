# Major-category architecture audit and proposal

**Project:** DeChimexPrints, v047-DesignReview-Fixed-v4  
**Date:** 22 September 2026  
**Status:** Pre-implementation source audit. The owner approved Option A, which is now implemented and verified. See `MAJOR_CATEGORIES.md` for delivered behavior, verification and deployment instructions. Findings below describe the audited baseline.

## 1. Existing structure discovered

### Domain and persistence

- `Domain/Entities/Category.cs` is a flat aggregate: name, slug, optional description, display order, active status, and ordering options. It has no parent relationship, category type discriminator, or hierarchy support.
- A category is explicitly a **product type**, not just a navigation label. Its ordering questions apply to every product belonging to it.
- `Product.CategoryId` is the existing required relationship. `CatalogConfigurations.cs` configures a restricted-delete foreign key from products to categories. Products do not have a separate grouping relationship.
- Categories have a filtered unique **slug** index for non-deleted rows. Create/edit handlers also reject duplicate names globally. That name comparison uses database equality; case behavior therefore depends on provider/collation. A name-only precheck does not cover every punctuation-to-slug collision.
- Categories and products use soft-delete query filters. Category deletion is already blocked when non-deleted products are assigned.
- The migration snapshot represents this flat model. The latest existing migration adds payment refund columns, not category hierarchy. No existing hierarchy migration or structure can simply be switched on.

### Admin workflows and ordering

- `AdminCategoriesController` provides create, edit, search, delete, and a separate ordering-options editor.
- Access is protected by `Categories.View` and `Categories.Manage`, with antiforgery protection on mutations.
- Creating a category automatically appends it after the current maximum display order. Editing preserves its position. This is an explicit existing rule with integration-test coverage; the UI intentionally has no manual order field.
- Admin product create/edit selects an existing category. It does not distinguish roots from leaves because every category currently represents a valid product type.
- `Category.ReplaceOptions` owns choice questions, required responses, ordering of questions, and conditional dependencies. Order responses are snapshotted so historical orders are independent of later option edits.

### Customer discovery, shopping, and filtering

- `/products` is public browsing; `/portal/shop` is the customer shopping/ordering surface. Both must receive the hierarchy rather than changing only the public catalog.
- Both listing views currently render flat category links and preserve category/search/sort through pagination.
- Public shared layout receives active categories through `StoreControllerBase`; its category links also need to remain compatible.
- `ProductQueries.SearchAsync` already joins products to categories. This is the clean place to add a major-category filter without denormalizing product data.
- The shared filter supports category, featured, active-only, minimum/maximum price, search, sorting and pagination. Public/portal controllers currently expose category, search and price-sort selection, not every query-side option.
- Active-only listing excludes inactive products and inactive categories.
- **Related filtering defect found:** the public and portal controllers currently apply price sorting after pagination. The query layer already supports sorting before pagination. The proposed implementation would wire the existing sort selection into that query behavior, with a multi-page regression test, rather than invent another sorting system.
- The shared query also serves admin order, invoice and procurement product pickers. An omitted major-category filter must preserve their existing behavior.

### Existing tests inspected

- `CategoryManagementTests`: automatic append, preserving position on edit, admin name/description search.
- Category handler unit tests: duplicate creation, missing category, deletion, in-use deletion rejection, cache invalidation.
- `ProductRulesTests`: positive price, required image, edit persistence, duplicate product names/slug resolution, last-image protection.
- Storefront, portal-shop, custom-order and design-review integration tests exercise connected customer/order workflows.
- SQL Server query-translation guards exist because InMemory previously concealed translation bugs.
- The handoff also requires model/snapshot consistency and an empty probe migration for every model change.

These are source findings, not a claim that tests were rerun during this audit.

## 2. Options compared

| Criterion | A — Separate MajorCategory | B — Parent/child Category | C — Group name or settings-based map |
|---|---|---|---|
| Domain fit | Separates discovery groups from product types and ordering questions | Mixes navigation-only roots with product-bearing categories | Simple label but weak identity and lifecycle |
| Database complexity | One small table plus nullable Category foreign key/index | One self-FK, but also root/leaf rules and likely a discriminator | Few schema additions, but no robust relational integrity |
| Admin usability | Small major-category screen; reuse category forms for assignment | Reusable CRUD, but every option/product picker needs type awareness | Renames, membership and duplicate handling become fragile |
| Filtering | Predicate on the existing joined category | Simple for exactly two levels; recursive logic if generalized | String matching or settings parsing |
| Extensibility | Clear two-level model; room for group metadata | Best if arbitrary-depth taxonomy is a real requirement | Poor once URLs, descriptions, validation and concurrency matter |
| Migration | Additive; all old CategoryIds remain valid | Additive schema, but ambiguous unassigned leaf versus root | Initially small; later conversion to proper identities is harder |
| Existing-behavior risk | Lowest: old categories keep their current meaning | Higher: must prohibit products/options on roots everywhere | Weak deletion, reassignment and uniqueness guarantees |
| Redundant data | None on products | None on products | Risks repeated group metadata or a separate mapping store |

## 3. Recommendation: Option A

Use **MajorCategory → existing Category → Product**.

This recommendation follows the audit, not the assumption that another table is always preferable. Here, the existing Category is already a specialized product-type aggregate with ordering behavior. A self-referencing tree would save one table but add semantic restrictions throughout product selection, options management, navigation and validation. The requested hierarchy has exactly two category levels; arbitrary-depth taxonomy is not needed.

Proposed minimal schema:

- `MajorCategory`: identity/audit/soft-delete fields using existing conventions; required name (120-character limit), optional description (1,000-character limit), automatic display order, and a normalized uniqueness key for predictable duplicate-name enforcement.
- `Category.MajorCategoryId`: nullable foreign key with restricted physical deletion and an index. Existing categories remain valid with a null assignment.
- **No MajorCategoryId on Product.** The major category is derived through its category.
- No major-category ordering questions, product assignments, new tree infrastructure, or many-to-many taxonomy.

## 4. Proposed user-facing behavior

### Admin

- Add a Major Categories section using existing category permissions and existing MVC/CQRS patterns.
- Create with name only or name and description; edit; view associated subcategories; delete only when no non-deleted categories remain assigned.
- Add an optional Major Category selector to the **existing category create/edit form**. This is also the single workflow for assigning, moving or unassigning an existing category.
- Major-category detail links to existing category edit/options pages; do not duplicate those editors.
- Existing category list gets a major-category column/filter and an explicit Unassigned filter.
- Product selection continues submitting CategoryId only; labels/grouping can show the hierarchy for clarity without adding a second persisted product relationship.

### Shopping

- Group category navigation in both `/products` and `/portal/shop` under major-category headings.
- A major-category heading is a browse link across its active subcategories; a subcategory link narrows to that category.
- Preserve unassigned categories in an **Other categories** navigation section. This is a display label, not a fabricated database row.
- Hide inactive categories as today; omit empty headings from regular navigation. A valid major-category direct link with no visible products gets a helpful empty state, not unrelated products.
- Use existing shareable query-string routes, e.g. `/products?majorCategoryId=<guid>`. GUID-based links remain stable after renaming; no new slug-routing subsystem is necessary.
- Keep existing category-only links working unchanged.
- Preserve all selected filters through search, sorting and pagination. Add the major filter to the shared query without changing default callers.

### Combined selection

- Major only: products whose category belongs to that major.
- Category only: the existing category filter.
- Both: an **intersection**, never one filter silently overriding the other.
- A mismatched pair yields no products and a clear explanation/reset action. Changing major category in the normal UI clears an incompatible category selection; manually supplied URLs remain strictly intersected.
- Unknown/deleted major-category IDs never silently expand to the entire catalog.

## 5. Integrity and migration rules

1. Major names are required, trimmed and case-insensitively unique among non-deleted majors, with application validation and a database uniqueness constraint. Renaming to the same normalized name is allowed for the same record.
2. Preserve the existing **global category-name/slug uniqueness policy**. This is stricter than uniqueness within a major category and avoids a new duplicate-name migration and ambiguous product selectors. Do not introduce same-named categories in different groups unless separately requested.
3. Unassigned categories remain valid and visible; migration must not guess their grouping from names.
4. Block deleting a major with assigned categories, including inactive categories. Reassign/unassign first. No cascade deletion of categories or products.
5. Preserve existing category deletion protection when products are assigned.
6. Moving a category updates only its major-category relationship. CategoryId, options, product relationships, historical order responses and display order stay unchanged.
7. Invalidate relevant navigation/category caches immediately after assignment, movement, rename and deletion.
8. Use an additive migration: create the majors table, add a nullable FK/index, and leave existing product/category rows intact. Verify generated SQL, snapshot consistency, an empty probe scaffold and SQL query translation. Apply against a relational test database if available; disclose explicitly if actual SQL Server execution cannot be verified.
9. Handle concurrent duplicate writes through database constraints and friendly conflict messages, rather than relying only on a precheck.

### Ordering decision

Use the existing **automatic append and preserve-on-edit convention** for majors. Display their child categories by their existing DisplayOrder, then name. Do not introduce drag-and-drop or manual ranking; the current application intentionally removed manual category ordering. Moving a child preserves its stored position.

## 6. Implementation verification and delivery plan

- Domain/application tests: required and maximum-length names/descriptions; normalized duplicates; invalid/deleted parent; rename; assign/move/unassign; in-use deletion; cache invalidation; authorization and antiforgery.
- Query/integration tests: major/category/intersection/mismatch filters, search/price/featured combinations, inactive/deleted records, unassigned categories, stable links and pagination/sort across more than 20 products.
- Migration checks: existing products remain valid; no destructive Up operations; snapshot consistency; empty probe; SQL Server translation guard extensions.
- Browser E2E: admin creates majors with and without descriptions → creates/assigns categories → creates/edits products → customer browses major and subcategory → combined filtering → complete category-option order submission → move category → confirm products/options still work → deletion/validation checks.
- Rerun connected existing product/category/order regressions and full suite.
- After fixes, restart with a clean dataset and perform one final full E2E run. Only that run supplies the numbered screenshots in `evidence/`.
- Update AI_HANDOFF, commit, create the **entire solution ZIP**, retain the supplied appsettings keys per standing instruction, and verify archive contents. The ZIP remains private because it contains credentials.

## Decision made before implementation

The owner approved **Option A**, including nullable initial assignment, preserving globally unique category names, automatic major ordering, and stable GUID query-string links. Implementation followed this decision.
