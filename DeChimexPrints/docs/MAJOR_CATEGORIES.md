# Major categories — implementation and deployment

## Architecture decision

The existing `Category` is a product-type aggregate with category-specific ordering
questions, not a generic tree node. The owner approved **Option A** after the source
audit: a separate `MajorCategory` parent above the existing `Category`.

```
MajorCategory (discovery group)
    └── Category (existing product type / subcategory, ordering questions)
            └── Product (unchanged CategoryId)
```

Only `Category` gains a nullable `MajorCategoryId`; products store no redundant
major-category relationship. A new small table uses existing audit and soft-delete
conventions. See `MAJOR_CATEGORY_ARCHITECTURE_AUDIT.md` for the A/B/C comparison.
A generic parent/child Category tree would require root-versus-leaf restrictions
across existing product and option workflows. A string/settings mapping would lack
robust identity, integrity and rename behavior. Neither was chosen.

## Using the feature

1. Open **Admin → Major categories**. Create a group with a name, with or without a
   description. Groups use automatic append ordering; edits retain position.
2. Open an existing category's edit form, or create a category. Choose its optional
   **Major category**. The same existing form assigns, moves or unassigns it.
3. Major-category detail shows associated categories and links to the existing
   ordering-options editor. The category list supports major filtering and
   **Unassigned only**. There is no duplicate subcategory editor.
4. Continue creating/editing products against a category. Dropdown labels show
   `Major → Category`; submitted/stored values remain CategoryId only.
5. Public `/products` and customer `/portal/shop` show grouped discovery navigation,
   including on mobile. Selecting a group browses all its active subcategories;
   selecting a leaf narrows to it. Unassigned categories remain under Other categories.

### URLs and filter rules

- `/products?majorCategoryId=<guid>` is a stable shareable major URL, unchanged by rename.
- `/portal/shop?majorCategoryId=<guid>` is the equivalent signed-in shopping URL.
- Existing `?categoryId=<guid>` URLs still work.
- Combining major and category uses intersection. An incompatible pair returns no
  products and an explanatory message; it never broadens to unrelated products.
- Group navigation clears a previously selected child; child navigation includes its
  own parent. Search/sort/pagination retain selections. Unknown/deleted groups show no
  products; malformed IDs are rejected.
- Existing shared query-side price/featured/active/search filters continue composing
  with the major filter. Existing public/portal search and sort controls are preserved.
- Price sort now uses the existing query layer **before** pagination, with ID tie-breaks,
  rather than sorting each already-selected page independently.

## Validation and integrity

- Major name: required, trimmed, max 120 characters; description optional, max 1,000.
- Case-normalized major names are unique among non-deleted groups, protected by a
  database filtered unique index as well as the application check. SQL duplicate
  write errors for category/major-category entities become friendly conflicts.
- Existing global category name/slug uniqueness policy is preserved (stricter than
  uniqueness within a group). This feature does not enable duplicate leaf names in
  different groups.
- Existing categories may remain unassigned. No migration guesses assignments from
  names, changes product IDs, or rewrites historical orders.
- Major deletion is blocked while non-deleted categories remain assigned, including
  inactive ones. Move/unassign them first. Empty majors are soft-deleted.
- Existing category deletion protection for assigned products remains intact.
- Moves preserve CategoryId, products, category options, historical order responses
  and category DisplayOrder. Cache invalidation refreshes labels/navigation immediately.
- Existing update commands that omit the new assignment intent preserve membership;
  the admin form explicitly indicates when membership is being edited or cleared.
- Management uses existing `categories.view` / `categories.manage` permissions and
  antiforgery protection. Read-only staff can view but cannot mutate.

## Migration / installation

Back up the database, replace the **entire solution** with this deliverable, restore
packages and apply migrations using the normal EF workflow. From `DeChimexPrints/`:

```bash
dotnet restore DeChimex.sln
dotnet build DeChimex.sln
dotnet ef database update --project src/DeChimex.Persistence --startup-project src/DeChimex.Host
```

Configure the intended SQL Server connection normally before running the update.
The new migration is **20260922025355_AddMajorCategories**. It creates the majors
table, adds nullable `Categories.MajorCategoryId`, indexes it, and adds a restricted
FK. Its Up path does not delete/update existing product/category data.

`database/20260922-MajorCategories-Upgrade.sql` is the generated incremental SQL
for databases already at `20260921100000_AddPaymentRefundColumns`. It is not an
idempotent full-chain script; prefer the EF command above, which uses migration
history and applies any outstanding preceding migrations in order. Do not scaffold
another migration for these already-shipped model changes.

## Final verification

- **589 automated tests passed:** 318 unit + 271 integration, zero failures/skips.
- **51 feature browser checks passed** on fresh app state, tag `finalmajor`, with
  **28 ordered screenshots** in root `evidence/`.
- **36 connected design-review browser checks passed** on a separate fresh boot.
- **14 ZIP configuration-guard validations passed**.
- Tests cover major create/edit/boundaries/duplicates, assignment/move/unassignment,
  deletion rules, product/options preservation, filtering combinations, cache
  refresh, price sorting across 25 products, invalid IDs, authorization and CSRF.
- New real SQLite tests use the production catalog mappings to check unique/FK
  constraints and products/category membership across reopened connections.
- SQL Server translation guards cover the new queries and combined filter shape.
  Their exception handling now only accepts actual SQL connection failures, not
  arbitrary exceptions. The whole existing guard suite passes.
- EF reports no pending model changes; a probe migration produced empty Up/Down.

**Verification boundary:** actual app browser tests use InMemory, and Brevo uses a
local stub. Real catalog durability/constraints were verified with SQLite, not a
live SQL Server. SQL Server upgrade SQL and translation were checked offline; the
migration has not been executed against a live SQL Server in this sandbox.

## Packaging

The ZIP contains the full solution, audit, migration, tests, handoff, E2E scripts
and final evidence, not only changed files. It intentionally retains the previously
supplied provider keys in both appsettings files per owner instruction. **Keep it
private.** Continue using `tools/make-deliverable-zip.sh` for future packages.
