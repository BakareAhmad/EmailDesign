using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Cart;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Application.Features.Orders;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Products.Queries;
using DeChimex.Application.Features.Reviews.Queries;
using DeChimex.Contracts.Orders;
using DeChimex.Contracts.Cart;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Models.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>
/// The customer portal Shop: the ONLY surface where product ordering happens (owner
/// directive 2026-09-10). The public /products section is browse-only; ordering —
/// product page, add-to-cart and the custom order-request form — lives here so a
/// signed-in customer never leaves their account area to order. The public
/// /products/{id}/order endpoints are thin redirects into this section.
/// Read-only money: no client-supplied prices anywhere.
/// </summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/shop")]
public sealed class PortalShopController(
    ICategoryOptionsQueries categoryOptions,
    IFileStorageService storage,
    ICustomerRepository customers,
    IWishlistRepository wishlist) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, Guid? categoryId = null, string? search = null, string? sort = null, Guid? majorCategoryId = null)
    {
        if (!ModelState.IsValid) return BadRequest("Invalid product filter.");
        ViewData["Title"] = "Shop";
        ViewData["Heading"] = "Shop products";
        ViewData["ActiveNav"] = "shop";

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
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        ViewBag.WishlistProductIds = (await wishlist.GetForCustomerAsync(customer.Id, HttpContext.RequestAborted))
            .Select(w => w.ProductId).ToHashSet();
        return View(items);
    }

    // ------------------------------------------------------------------
    // Product page (browse + order entry point) inside the portal.
    // ------------------------------------------------------------------

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Product(Guid id)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null || !product.IsActive)
            return NotFound();

        ViewData["Title"] = product.Name;
        ViewData["Heading"] = "Shop products";
        ViewData["ActiveNav"] = "shop";
        ViewBag.CategoryHasOptions = product.CategoryId != Guid.Empty
            && await categoryOptions.CategoryHasActiveOptionsAsync(product.CategoryId, HttpContext.RequestAborted);
        ViewBag.UseCustomOrder = product.IsCustomizable || ViewBag.CategoryHasOptions is true;
        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        ViewBag.InWishlist = await wishlist.FindAsync(customer.Id, id, HttpContext.RequestAborted) is not null;
        // Same product experience as the public page (shared _ProductDetail partial):
        // approved reviews, the purchased-only review gate and related products.
        ViewBag.Reviews = await Sender.Send(new GetProductReviewsQuery(id, ApprovedOnly: true));
        ViewBag.SignedIn = true;
        ViewBag.CanReview = await Sender.Send(new HasPurchasedProductQuery(id, customer.Id));
        ViewBag.HasReviewed = await Sender.Send(new HasProductReviewQuery(id, customer.Id));
        ViewBag.Related = await Sender.Send(new GetFeaturedProductsQuery(4));
        return View(product);
    }

    /// <summary>Adds a plain product line to the cart, then hands the customer to the
    /// existing cart/checkout flow (which renders inside the portal for customers).</summary>
    [HttpPost("{id:guid}/add-to-cart")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddToCart(Guid id, int quantity, bool buyNow = false)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null || !product.IsActive)
            return NotFound();
        if (quantity < 1) quantity = 1;

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        await Sender.Send(new AddCartItemCommand(customer.Id, new AddCartItemRequest(
            "product", id, quantity, null)));
        // "Buy now" is the same cart line + the same checkout — it only skips the cart
        // page (no second order path; AddCartItemCommand's custom-product guard still applies).
        if (buyNow)
            return RedirectToAction("Index", "Checkout");
        TempData["Success"] = $"{product.Name} added to your cart.";
        return RedirectToAction("Index", "StoreCart");
    }

    // ------------------------------------------------------------------
    // Custom order request (configure → staff review), portal-native.
    // Ported verbatim from the public StoreProductsController (2026-09-10):
    // the public endpoints are now thin redirects into this section.
    // ------------------------------------------------------------------

    [HttpGet("{id:guid}/order")]
    public async Task<IActionResult> Order(Guid id)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null || !product.IsActive)
            return NotFound();

        var options = product.CategoryId == Guid.Empty
            ? new List<CategoryOptionDto>()
            : await categoryOptions.GetForCategoryAsync(product.CategoryId, HttpContext.RequestAborted);
        if (!product.IsCustomizable && options.Count == 0)
            return RedirectToAction(nameof(Product), new { id });

        ViewData["Title"] = $"Order {product.Name}";
        ViewData["Heading"] = "Shop products";
        ViewData["ActiveNav"] = "shop";
        ViewBag.Options = options;
        ViewBag.Draft = LoadDraft();
        return View("OrderForm", product);
    }

    /// <summary>Submits an order request (responses + optional file uploads) for staff review.</summary>
    [HttpPost("{id:guid}/order")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitOrder(Guid id, OrderRequestFormViewModel model, IFormCollection form)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null || !product.IsActive)
            return NotFound();

        var options = product.CategoryId == Guid.Empty
            ? new List<CategoryOptionDto>()
            : await categoryOptions.GetForCategoryAsync(product.CategoryId, HttpContext.RequestAborted);

        var quantity = model.Quantity < 1 ? 1 : model.Quantity;
        if (model.Quantity > 10_000)
        {
            TempData["Error"] = "Quantity must be between 1 and 10,000.";
            return BackToOrderForm(id, model, options, form);
        }

        // Assemble the responses from the raw form ("opt_{optionId}" keys).
        var responses = new List<OrderRequirementInput>();
        foreach (var option in options)
        {
            var raw = form[$"opt_{option.Id}"];
            var type = option.Type;
            string value;
            switch (type)
            {
                case "SingleChoice":
                case "MultipleChoice":
                    var selected = raw.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
                    value = System.Text.Json.JsonSerializer.Serialize(selected);
                    break;
                case "FileUpload":
                    value = "[]"; // file URLs are appended after upload below
                    break;
                default:
                    value = raw.FirstOrDefault()?.Trim() ?? string.Empty;
                    break;
            }
            responses.Add(new OrderRequirementInput(option.Id, option.Question, type, value));
        }

        // Pre-check required answers BEFORE uploading anything, so an invalid request never
        // leaves orphan files in storage. FileUpload options count as answered when a file
        // was actually attached for them. NOTE: the option inputs are named "opt_{id}", so
        // their files arrive in form.Files alongside the general "files" field — both are
        // uploaded here, and only form.Files carries the complete set.
        var pendingFileOptionIds = form.Files
            .Where(f => f.Name.StartsWith("opt_", StringComparison.Ordinal))
            .Select(f => f.Name.Substring(4))
            .Where(t => Guid.TryParse(t, out _))
            .Select(Guid.Parse)
            .ToHashSet();
        var missingRequired = CustomOrderValidation.MissingRequiredQuestions(options, responses, pendingFileOptionIds);
        if (missingRequired.Count > 0)
        {
            TempData["Error"] = "Please answer the required questions: " + string.Join(", ", missingRequired) + ".";
            return BackToOrderForm(id, model, options, form);
        }

        // Upload any attached files (design references for FileUpload options plus a general
        // "attach a file" field) and record their metadata for the order request.
        var uploadedFiles = new List<OrderFileInput>();
        try
        {
            foreach (var file in form.Files)
            {
                if (file.Length <= 0) continue;
                using var stream = file.OpenReadStream();
                var uploaded = await storage.UploadAsync(new FileUploadRequest(
                    stream, file.FileName, file.ContentType, file.Length, FileCategory.OrderAttachment));
                uploadedFiles.Add(new OrderFileInput(
                    uploaded.StoredFileName, file.FileName, uploaded.Url, file.ContentType, uploaded.Size));

                // A file named "opt_{optionId}" also answers that option (JSON array of URLs).
                if (file.Name.StartsWith("opt_", StringComparison.Ordinal))
                {
                    var optionIdText = file.Name.Substring(4);
                    var target = responses.FirstOrDefault(r => r.CategoryOptionId?.ToString() == optionIdText);
                    if (target is not null && Guid.TryParse(optionIdText, out _))
                    {
                        var existing = DeserializeUrls(target.Value);
                        var updated = existing.Append(uploaded.Url).Distinct().ToArray();
                        var idx = responses.FindIndex(r => ReferenceEquals(r, target));
                        responses[idx] = target with { Value = System.Text.Json.JsonSerializer.Serialize(updated) };
                    }
                }
            }
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = (ex.Errors.Values.SelectMany(v => v).FirstOrDefault()
                ?? "One of the uploaded files could not be accepted.")
                + " Please re-attach your file and try again.";
            return BackToOrderForm(id, model, options, form);
        }

        try
        {
            var workspace = await Sender.Send(new SubmitOrderRequestCommand(new SubmitOrderRequest(
                id, quantity,
                string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
                responses, uploadedFiles)));

            TempData["Success"] = $"Order request {workspace.OrderNumber} submitted. Our team will review it shortly.";
            return RedirectToAction("Workspace", "PortalOrders", new { id = workspace.Id });
        }
        catch (ValidationAppException ex)
        {
            TempData["Error"] = ex.Errors.Values.SelectMany(v => v).FirstOrDefault()
                ?? "Please complete the required fields.";
            return BackToOrderForm(id, model, options, form);
        }
        catch (ConflictException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Product), new { id });
        }
    }

    /// <summary>Returns to the order form with the submitted answers preserved.</summary>
    private IActionResult BackToOrderForm(Guid id, OrderRequestFormViewModel model,
        IReadOnlyList<CategoryOptionDto> options, IFormCollection form)
    {
        var draft = new OrderFormDraft
        {
            Quantity = model.Quantity < 1 ? 1 : model.Quantity,
            Notes = model.Notes
        };
        foreach (var option in options)
        {
            var raw = form[$"opt_{option.Id}"].Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
            if (raw.Length > 0)
                draft.Responses[option.Id.ToString()] = raw;
        }
        StashDraft(draft);
        return RedirectToAction(nameof(Order), new { id });
    }

    private const string DraftKey = "OrderFormDraft";

    private void StashDraft(OrderFormDraft draft)
        => TempData[DraftKey] = System.Text.Json.JsonSerializer.Serialize(draft);

    private OrderFormDraft? LoadDraft()
    {
        if (TempData[DraftKey] is not string json) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<OrderFormDraft>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string[] DeserializeUrls(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
