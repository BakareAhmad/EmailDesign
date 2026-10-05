using DeChimex.Application.Common.Exceptions;

namespace DeChimex.Application.Features.Orders;

/// <summary>
/// Direct-purchase eligibility (owner decision 2026-09-09, CUSTOM_ORDER_FLOW_PLAN.md §1):
/// a product can go straight to checkout (Add to cart -> checkout) iff it is NOT
/// customizable and its category carries NO active ordering questions. Everything else
/// must use the custom order-request path ("Order / Customize"), so requirements and
/// pricing review can never be bypassed by adding a custom product to the cart.
///
/// There is deliberately no separate DirectPurchaseEnabled product flag — this derived
/// rule is the single source of truth (see DIRECT_CHECKOUT_EVALUATION.md §4).
/// </summary>
public static class DirectCheckout
{
    public static bool IsEligible(bool productIsCustomizable, bool categoryHasActiveOptions)
        => !productIsCustomizable && !categoryHasActiveOptions;

    /// <summary>Authoritative gate for the direct path; throws with a customer-safe message.</summary>
    public static void RequireEligible(string productName, bool productIsCustomizable, bool categoryHasActiveOptions)
    {
        if (!IsEligible(productIsCustomizable, categoryHasActiveOptions))
            throw new ConflictException("checkout.direct",
                $"“{productName}” needs customization — use “Order / Customize” on its product page instead of the cart.");
    }
}
