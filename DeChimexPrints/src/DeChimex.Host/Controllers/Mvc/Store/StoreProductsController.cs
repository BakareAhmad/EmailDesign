using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Application.Features.Orders;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Products.Queries;
using DeChimex.Application.Features.Reviews.Commands;
using DeChimex.Application.Features.Reviews.Queries;
using DeChimex.Application.Features.Wishlist;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Reviews;
using DeChimex.Contracts.Wishlist;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Models.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Store;

/// <summary>
/// Public product catalog: listing (search, category, pagination) and detail.
/// Browsing is anonymous; submitting a review or saving to a wishlist requires a
/// signed-in customer (authorized at the action level, not at the controller level,
/// so the [Authorize] attributes are never overridden by an earlier [AllowAnonymous]).
/// </summary>
[Route("products")]
public sealed class StoreProductsController(
    ICustomerRepository customers,
    IWishlistRepository wishlist,
    ICategoryOptionsQueries categoryOptions) : StoreControllerBase
{

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Index(int page = 1, Guid? categoryId = null, string? search = null, string? sort = null, Guid? majorCategoryId = null)
    {
        if (!ModelState.IsValid) return BadRequest("Invalid product filter.");
        ViewData["Title"] = "Products";
        ViewData["ActiveNav"] = "products";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();

        var filter = new ProductFilter
        {
            Page = page,
            PageSize = 20,
            Search = search,
            CategoryId = categoryId,
            MajorCategoryId = majorCategoryId,
            SortBy = sort is "low" or "high" ? "price" : null,
            SortDescending = sort == "high",
            ActiveOnly = true
        };
        var result = await Sender.Send(new SearchProductsQuery(filter));
        var categories = await Sender.Send(new GetCategoriesQuery(ActiveOnly: true));

        var items = result.Items.ToList();
        var majors = await Sender.Send(new DeChimex.Application.Features.Categories.GetMajorCategoriesQuery());
        ViewBag.MajorCategories = majors;
        ViewBag.MajorCategoryId = majorCategoryId;
        ViewBag.SelectedMajor = majors.FirstOrDefault(m => m.Id == majorCategoryId);
        ViewBag.FilterMismatch = majorCategoryId.HasValue && categoryId.HasValue
            && !categories.Any(c => c.Id == categoryId && c.MajorCategoryId == majorCategoryId);

        ViewBag.Categories = categories;
        ViewBag.CategoryId = categoryId;
        ViewBag.Search = search;
        ViewBag.Sort = sort;
        ViewBag.Page = result.Page;
        ViewBag.PageSize = result.PageSize;
        ViewBag.TotalRecords = result.TotalRecords;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        return View(items);
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Details(Guid id)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null || !product.IsActive)
            return NotFound();

        ViewData["Title"] = product.Name;
        ViewData["ActiveNav"] = "products";
        ViewBag.Business = await LoadBusinessInfoAsync();
        ViewBag.NavCategories = await LoadNavCategoriesAsync();
        ViewBag.Related = await Sender.Send(new GetFeaturedProductsQuery(4));
        ViewBag.Reviews = await Sender.Send(new GetProductReviewsQuery(id, ApprovedOnly: true));

        Customer? reviewer = null;
        var inWishlist = false;
        if (CurrentUser.UserId is Guid userId)
        {
            reviewer = await customers.GetByUserIdAsync(userId, HttpContext.RequestAborted);
            if (reviewer is not null)
                inWishlist = await wishlist.FindAsync(reviewer.Id, id, HttpContext.RequestAborted) is not null;
        }
        // Review eligibility = signed in AND has a qualifying purchase of this product
        // (paid-or-later order; direct and custom orders count identically).
        ViewBag.SignedIn = reviewer is not null;
        ViewBag.CanReview = reviewer is not null
            && await Sender.Send(new HasPurchasedProductQuery(id, reviewer.Id));
        ViewBag.HasReviewed = reviewer is null
            ? false
            : await Sender.Send(new HasProductReviewQuery(id, reviewer.Id));
        ViewBag.InWishlist = inWishlist;

        // Auto-detect route (§20): a category with ordering options or a customizable
        // product shows the "Order / Customize" CTA instead of a plain add-to-cart.
        ViewBag.CategoryHasOptions = product.CategoryId != Guid.Empty
            && await categoryOptions.CategoryHasActiveOptionsAsync(product.CategoryId, HttpContext.RequestAborted);
        ViewBag.UseCustomOrder = product.IsCustomizable || ViewBag.CategoryHasOptions is true;
        return View(product);
    }

    /// <summary>Order-request form: renders the category's configurable options dynamically.</summary>
    // ------------------------------------------------------------------
    // Ordering moved to the customer portal (owner directive 2026-09-10):
    // the public product section is browse-only. Any attempt to order here
    // is redirected into /portal/shop, where the product page, cart and the
    // custom order-request form live (guests are challenged to sign in by
    // the portal's [Authorize] and returned here after login).
    // ------------------------------------------------------------------

    // No [Authorize] here on purpose: an unauthenticated visitor should be redirected
    // straight into /portal/shop/... (the portal's own [Authorize] then challenges with
    // the PORTAL url as ReturnUrl, so post-login they land inside the shop, not here).
    [HttpGet("{id:guid}/order")]
    public IActionResult Order(Guid id)
        => RedirectToAction("Order", "PortalShop", new { id });

    [HttpPost("{id:guid}/order")]
    public IActionResult SubmitOrder(Guid id)
        => RedirectToAction("Order", "PortalShop", new { id });

    [HttpPost("reviews/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> AddReview(Guid id, ProductReviewFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please fix the highlighted review fields.";
            return RedirectToAction(nameof(Details), new { id });
        }

        try
        {
            await Sender.Send(new CreateReviewCommand(new CreateReviewRequest(
                id, model.Rating,
                string.IsNullOrWhiteSpace(model.Title) ? null : model.Title,
                string.IsNullOrWhiteSpace(model.Comment) ? null : model.Comment)));
            TempData["Success"] = "Thank you! Your review is in and will appear once approved by the team.";
        }
        catch (ConflictException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>A signed-in customer saves a product to their wishlist (idempotent).</summary>
    [HttpPost("wishlist/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> AddToWishlist(Guid id)
    {
        var customer = await ResolveCustomerForWishlistAsync();
        if (customer is null)
        {
            TempData["Error"] = "Please sign in to save items to your wishlist.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await Sender.Send(new AddWishlistItemCommand(customer.Id, new AddWishlistItemRequest(id)));
        TempData["Success"] = "Added to your wishlist.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>A signed-in customer removes a product from their wishlist.</summary>
    [HttpPost("wishlist/{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> RemoveFromWishlist(Guid id)
    {
        var customer = await ResolveCustomerForWishlistAsync();
        if (customer is null)
        {
            TempData["Error"] = "Please sign in to manage your wishlist.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await Sender.Send(new RemoveWishlistItemCommand(customer.Id, id));
        TempData["Success"] = "Removed from your wishlist.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<Customer?> ResolveCustomerForWishlistAsync()
    {
        return CurrentUser.UserId is Guid userId
            ? await customers.GetByUserIdAsync(userId, HttpContext.RequestAborted)
            : null;
    }
}
