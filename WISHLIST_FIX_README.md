# Wishlist controls restored — 23 September 2026

**Where to find it:** My account → Shop products. Both the product cards and the individual product page now show **Add to wishlist**. Saved products show a filled heart and **Remove from wishlist**.

## Fixes

- The public product details already supported saving, but the customer portal did not. The portal now uses the same existing wishlist commands, table and customer ownership rules.
- Adding/removing from the catalog preserves the current category, search, sort and page. The product page stays on the same product.
- Saved products have a **View product / order** link inside the portal. The empty wishlist's **Browse products** link also stays in the portal.
- Removing and re-adding restores the existing soft-deleted membership, avoiding the unique customer/product key conflict. The EF physical-existence check now includes soft-deleted rows so a restoration is not incorrectly promoted to an INSERT.
- Missing, inactive or deleted products cannot be newly saved through either portal or public add actions. CSRF protection, customer-only portal access and current-customer ownership are retained. External return URLs are rejected.
- Labels wrap on narrow screens; controls support keyboard activation and expose saved state accessibly.
- Wishlist saves do not create business-activity feed or notification noise. Existing cart/custom-order flows remain unchanged.

**No new database migration is needed for this wishlist fix.** All previous business activity implementation and its existing migration remain in the complete solution.

## Verification

- **616 automated tests passed:** 325 unit + 291 integration, zero failures/skips.
- **161 named browser checks passed:** 32 wishlist, 42 business activity, 51 category/ordering, 36 design review.
- SQLite relational verification confirms remove/re-add across fresh contexts keeps one persisted membership with the original ID and clears its deletion metadata.
- **12 ordered screenshots** from one final clean combined run; no old/debug screenshots are mixed in.

See [verification details](docs/wishlist/VERIFICATION.md) and [evidence index](docs/wishlist/EVIDENCE-INDEX.md).

The supplied Brevo and Paystack settings remain included as instructed. **Keep the complete solution ZIP private.**
