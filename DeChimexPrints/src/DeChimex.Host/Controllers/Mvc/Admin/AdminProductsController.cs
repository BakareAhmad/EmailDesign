using DeChimex.Host.Models.Admin;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Application.Features.Products.Commands;
using DeChimex.Application.Features.Products.Queries;
using DeChimex.Application.Features.Reviews.Commands;
using DeChimex.Application.Features.Reviews.Queries;
using DeChimex.Contracts.Products;
using DeChimex.Infrastructure.Options;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>Admin product management. Reuses the existing product queries/commands directly.</summary>
[Authorize(Policy = Permissions.Products.View)]
[Route("admin/products")]
public sealed class AdminProductsController : MvcControllerBase
{
    private readonly IFileStorageService _storage;
    private readonly StorageOptions _storageOptions;

    public AdminProductsController(IFileStorageService storage, IOptions<StorageOptions> storageOptions)
    {
        _storage = storage;
        _storageOptions = storageOptions.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, string? search = null)
    {
        ViewData["Title"] = "Products";
        ViewData["Heading"] = "Products";
        ViewData["ActiveNav"] = "products";

        var filter = new ProductFilter
        {
            Page = page,
            PageSize = 20,
            Search = search,
            ActiveOnly = false // show all products including inactive
        };
        var result = await Sender.Send(new SearchProductsQuery(filter));

        ViewBag.Search = search;
        ViewBag.Page = result.Page;
        ViewBag.PageSize = result.PageSize;
        ViewBag.TotalRecords = result.TotalRecords;
        ViewBag.TotalPages = result.TotalPages;
        ViewBag.HasPrevious = result.HasPrevious;
        ViewBag.HasNext = result.HasNext;
        ViewBag.CanCreate = User.HasClaim(Permissions.ClaimType, Permissions.Products.Create);
        ViewBag.CanUpdate = User.HasClaim(Permissions.ClaimType, Permissions.Products.Update);
        ViewBag.CanDelete = User.HasClaim(Permissions.ClaimType, Permissions.Products.Delete);
        return View(result.Items);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Products.Create)]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New product";
        ViewData["Heading"] = "Products";
        ViewData["ActiveNav"] = "products";
        await PopulateCategoriesAsync();

        var existingSkus = await Sender.Send(new GetAllProductSkusQuery());
        ViewBag.ExistingSkus = existingSkus;
        return View(new ProductFormViewModel
        {
            InitialQuantity = 0,
            LowStockThreshold = 5,
            // Pre-fill a valid, unique suggestion so the form works even before the
            // client-side live generator runs (and for the no-JS case).
            Sku = ProductSkuGenerator.Suggest(null, existingSkus)
        });
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Products.Create)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProductFormViewModel model)
    {
        ProductDto? product = null;
        try
        {
            // Expose the existing SKUs to the view so the live regenerator stays unique
            // even when the form is re-rendered after a validation error.
            var existingSkus = await Sender.Send(new GetAllProductSkusQuery());
            ViewBag.ExistingSkus = existingSkus;

            // At least one real image file is required for a new product: the storefront
            // has no placeholder fallback, so a product without an image is not allowed.
            if (model.Images is null || !model.Images.Any(f => f is { Length: > 0 }))
                ModelState.AddModelError(nameof(model.Images), "Upload at least one product image.");

            if (!ModelState.IsValid)
            {
                await PopulateCategoriesAsync();
                return View(model);
            }

            product = await Sender.Send(new CreateProductCommand(new CreateProductRequest(
                model.Name.Trim(), model.Sku.Trim(), model.Description, model.CategoryId,
                model.SellingPrice, model.IsCustomizable, model.IsFeatured,
                model.InitialQuantity, model.LowStockThreshold)));

            await UploadImagesAsync(product.Id, model.Images, makeFirstPrimary: true);
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateCategoriesAsync();
            return View(model);
        }
        catch (ValidationAppException ex)
        {
            foreach (var (field, messages) in ex.Errors)
                ModelState.AddModelError(field, messages[0]);
            await PopulateCategoriesAsync();
            return View(model);
        }
        catch (DbUpdateException)
        {
            // Belt-and-braces: a database-rejected save (unique index on a value not covered
            // by the up-front resolvers) must never be silently dropped — redisplay instead.
            ModelState.AddModelError(string.Empty,
                "The product could not be saved because another record already uses one of its unique values (name or SKU). Adjust the values and save again.");
            await PopulateCategoriesAsync();
            return View(model);
        }

        TempData["Success"] = "Product created.";
        return RedirectToAction(nameof(Details), new { id = product!.Id });
    }

    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Products.Update)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null) return NotFound();

        ViewData["Title"] = "Edit product";
        ViewData["Heading"] = "Products";
        ViewData["ActiveNav"] = "products";
        ViewBag.NeedsImage = product.Images.Count == 0;
        await PopulateCategoriesAsync();
        return View(new ProductFormViewModel
        {
            Id = product.Id,
            Name = product.Name,
            Sku = product.Sku,
            Description = product.Description,
            CategoryId = product.CategoryId,
            SellingPrice = product.SellingPrice,
            IsCustomizable = product.IsCustomizable,
            IsFeatured = product.IsFeatured,
            InitialQuantity = product.AvailableQuantity,
            LowStockThreshold = product.LowStockThreshold
        });
    }

    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Products.Update)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ProductFormViewModel model)
    {
        try
        {
            // A product must never end up without an image. Re-uploading is optional only
            // while the product already has at least one image stored.
            var existing = await Sender.Send(new GetProductByIdQuery(id));
            if (existing is null) return NotFound();
            var hasImages = existing.Images.Count > 0;
            ViewBag.NeedsImage = !hasImages;
            if (!hasImages && (model.Images is null || !model.Images.Any(f => f is { Length: > 0 })))
                ModelState.AddModelError(nameof(model.Images), "This product has no image. Upload at least one product image.");

            if (!ModelState.IsValid)
            {
                await PopulateCategoriesAsync();
                return View(model);
            }

            await Sender.Send(new UpdateProductCommand(id, new UpdateProductRequest(
                model.Name.Trim(), model.Description, model.CategoryId,
                model.SellingPrice, model.IsCustomizable, model.IsFeatured)));

            await UploadImagesAsync(id, model.Images, makeFirstPrimary: false);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateCategoriesAsync();
            return View(model);
        }
        catch (ValidationAppException ex)
        {
            foreach (var (field, messages) in ex.Errors)
                ModelState.AddModelError(field, messages[0]);
            await PopulateCategoriesAsync();
            return View(model);
        }
        catch (DbUpdateException)
        {
            // Belt-and-braces: a save that the database rejected (e.g. a unique index on a
            // value not covered by the up-front resolvers) must NEVER be silently dropped —
            // redisplay the form with the submitted values and a clear message instead.
            ModelState.AddModelError(string.Empty,
                "The product could not be saved because another record already uses one of its unique values (name or SKU). Adjust the values and save again.");
            await PopulateCategoriesAsync();
            return View(model);
        }

        TempData["Success"] = "Product updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null) return NotFound();

        ViewData["Title"] = product.Name;
        ViewData["Heading"] = "Products";
        ViewData["ActiveNav"] = "products";
        ViewBag.CanUpdate = User.HasClaim(Permissions.ClaimType, Permissions.Products.Update);
        ViewBag.CanDelete = User.HasClaim(Permissions.ClaimType, Permissions.Products.Delete);
        ViewBag.Reviews = await Sender.Send(new GetProductReviewsQuery(id, ApprovedOnly: false));
        return View(product);
    }

    [HttpPost("reviews/{id:guid}/approve/{reviewId:guid}")]
    [Authorize(Policy = Permissions.Products.Update)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveReview(Guid id, Guid reviewId)
    {
        await Sender.Send(new ApproveReviewCommand(reviewId));
        TempData["Success"] = "Review approved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("reviews/{id:guid}/reject/{reviewId:guid}")]
    [Authorize(Policy = Permissions.Products.Update)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectReview(Guid id, Guid reviewId)
    {
        await Sender.Send(new RejectReviewCommand(reviewId));
        TempData["Success"] = "Review hidden.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("reviews/{id:guid}/delete/{reviewId:guid}")]
    [Authorize(Policy = Permissions.Products.Update)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReview(Guid id, Guid reviewId)
    {
        await Sender.Send(new DeleteReviewCommand(reviewId));
        TempData["Success"] = "Review deleted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("images/{id:guid}/remove/{imageId:guid}")]
    [Authorize(Policy = Permissions.Products.Update)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveImage(Guid id, Guid imageId)
    {
        var product = await Sender.Send(new GetProductByIdQuery(id));
        if (product is null) return NotFound();

        var image = product.Images.FirstOrDefault(i => i.Id == imageId);
        try
        {
            await Sender.Send(new RemoveProductImageCommand(id, imageId));
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            // The domain refuses to remove the last remaining image.
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        // Best-effort cleanup of the stored file so deleted images do not linger on disk.
        if (image is not null)
            await TryDeleteStoredFileAsync(image.Url);

        TempData["Success"] = "Image removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("images/{id:guid}/primary/{imageId:guid}")]
    [Authorize(Policy = Permissions.Products.Update)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetImagePrimary(Guid id, Guid imageId)
    {
        await Sender.Send(new SetProductImagePrimaryCommand(id, imageId));
        TempData["Success"] = "Primary image updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Re-applies the product image order; the new first image becomes the primary.</summary>
    [HttpPost("images/{id:guid}/reorder")]
    [Authorize(Policy = Permissions.Products.Update)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReorderImages(Guid id, List<Guid> orderedIds)
    {
        await Sender.Send(new ReorderProductImagesCommand(id, orderedIds));
        TempData["Success"] = "Image order updated; the first image is now the primary.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Products.Delete)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Sender.Send(new DeleteProductCommand(id));
        TempData["Success"] = "Product deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task UploadImagesAsync(Guid productId, IFormFile[]? files, bool makeFirstPrimary)
    {
        var index = 0;
        foreach (var file in files ?? Array.Empty<IFormFile>())
        {
            if (file.Length <= 0) continue;
            using var stream = file.OpenReadStream();
            var uploaded = await _storage.UploadAsync(new FileUploadRequest(
                stream, file.FileName, file.ContentType, file.Length, FileCategory.ProductImage));
            await Sender.Send(new AddProductImageCommand(productId, uploaded.Url, null, index == 0 && makeFirstPrimary));
            index++;
        }
    }

    private async Task TryDeleteStoredFileAsync(string url)
    {
        try
        {
            var publicBase = _storageOptions.PublicBaseUrl.TrimEnd('/');
            if (string.IsNullOrEmpty(publicBase) || !url.StartsWith(publicBase, StringComparison.OrdinalIgnoreCase))
                return;
            var storedName = url.Substring(publicBase.Length).TrimStart('/');
            if (string.IsNullOrEmpty(storedName)) return;
            await _storage.DeleteAsync(storedName);
        }
        catch
        {
            // The database row is the source of truth; a leftover file is not user visible.
        }
    }

    private async Task PopulateCategoriesAsync()
    {
        var categories = await Sender.Send(new GetCategoriesQuery(ActiveOnly: false));
        ViewBag.Categories = categories
            .Select(c => new SelectListItem(c.MajorCategoryName == null ? c.Name : $"{c.MajorCategoryName} → {c.Name}", c.Id.ToString()))
            .ToList();
    }
}
