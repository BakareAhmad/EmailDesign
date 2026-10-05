using System.Net;
using System.Net.Http.Headers;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Product data-integrity rules (owner directive 2026-09-09):
///  - a product cannot be created or updated with a zero (or negative) selling price;
///  - a product must have at least one image: creation requires an upload, editing an
///    image-less product requires an upload, and the last remaining image cannot be removed;
///  - the storefront no longer falls back to the hero banner for image-less products.
/// </summary>
public class ProductRulesTests : IClassFixture<ProductRulesTests.ProductRulesFactory>
{
    /// <summary>Redirects uploads to a temp folder so tests never write into the repo's wwwroot.</summary>
    public sealed class ProductRulesFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Storage:LocalRootPath",
                Path.Combine(Path.GetTempPath(), "dechimex-test-uploads-" + Guid.NewGuid().ToString("N")));
            base.ConfigureWebHost(builder);
        }
    }

    private readonly ProductRulesFactory _factory;

    public ProductRulesTests(ProductRulesFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> OwnerBrowserAsync() =>
        await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

    private async Task<Guid> SeedCategoryAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create(name, "test category", 1);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    /// <summary>Builds the multipart create/edit form body the admin product form posts.</summary>
    private static async Task<MultipartFormDataContent> BuildProductFormAsync(
        HttpClient client, string formUrl, string name, Guid categoryId, decimal price,
        bool includeImage, string? sku = null)
    {
        var page = await client.GetAsync(formUrl);
        var html = await page.Content.ReadAsStringAsync();
        var token = TestSeed.ExtractAntiforgeryToken(html);

        // The real form always pre-fills a suggested SKU (the field is implicitly required
        // by the non-nullable view-model property), so mirror that instead of posting empty.
        var effectiveSku = sku ?? "TST-" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();

        var form = new MultipartFormDataContent
        {
            { new StringContent(name), "Name" },
            { new StringContent(effectiveSku), "Sku" },
            { new StringContent("Integration-test product"), "Description" },
            { new StringContent(categoryId.ToString()), "CategoryId" },
            { new StringContent(price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)), "SellingPrice" },
            { new StringContent("5"), "InitialQuantity" },
            { new StringContent("2"), "LowStockThreshold" },
            { new StringContent(token), "__RequestVerificationToken" }
        };

        if (includeImage)
        {
            // Smallest valid 1x1 PNG.
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "Images", "product.png");
        }

        return form;
    }

    [Fact]
    public async Task CreateProduct_WithZeroPrice_IsRejected()
    {
        var client = await OwnerBrowserAsync();
        var categoryId = await SeedCategoryAsync("Zero Price Cat");

        var form = await BuildProductFormAsync(client, "/admin/products/create", "Zero Price Product", categoryId, 0m, includeImage: true);
        var response = await client.PostAsync("/admin/products/create", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form must re-render with the error instead of redirecting");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("greater than zero");
    }

    [Fact]
    public async Task CreateProduct_WithNegativePrice_IsRejected()
    {
        var client = await OwnerBrowserAsync();
        var categoryId = await SeedCategoryAsync("Neg Price Cat");

        var form = await BuildProductFormAsync(client, "/admin/products/create", "Negative Price Product", categoryId, -5m, includeImage: true);
        var response = await client.PostAsync("/admin/products/create", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("greater than zero");
    }

    [Fact]
    public async Task CreateProduct_WithoutImage_IsRejected()
    {
        var client = await OwnerBrowserAsync();
        var categoryId = await SeedCategoryAsync("No Image Cat");

        var form = await BuildProductFormAsync(client, "/admin/products/create", "No Image Product", categoryId, 1500m, includeImage: false);
        var response = await client.PostAsync("/admin/products/create", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form must re-render with the error instead of redirecting");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Upload at least one product image");
    }

    [Fact]
    public async Task CreateProduct_WithPriceAndImage_Succeeds()
    {
        var client = await OwnerBrowserAsync();
        var categoryId = await SeedCategoryAsync("Happy Path Cat");

        var form = await BuildProductFormAsync(client, "/admin/products/create", "Complete Product", categoryId, 2500.50m, includeImage: true);
        var response = await client.PostAsync("/admin/products/create", form);

        var debugBody = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadAsStringAsync() : string.Empty;
        var titleMatch = System.Text.RegularExpressions.Regex.Match(debugBody, "<title>([^<]*)</title>");
        var summary = string.Join(" | ", System.Text.RegularExpressions.Regex.Matches(debugBody, @"field-error[^>]*>\s*([^<]+?)\s*<").Select(m => m.Groups[1].Value));
        var hasForm = debugBody.Contains("New product");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "create should succeed; title={0}, form={1}, errors={2}, len={3}",
            titleMatch.Groups[1].Value, hasForm, summary, debugBody.Length);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith("/admin/products/");

        var details = await client.GetAsync(location);
        details.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await details.Content.ReadAsStringAsync();
        body.Should().Contain("Complete Product");
        body.Should().Contain("/uploads/productimage/", "the uploaded image must be attached to the product");
    }

    [Fact]
    public async Task EditProduct_WithZeroPrice_IsRejected()
    {
        var client = await OwnerBrowserAsync();
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "Edit Price Product", 1200m);

        var form = await BuildProductFormAsync(client, $"/admin/products/edit/{productId}", "Edit Price Product",
            await GetCategoryIdAsync(productId), 0m, includeImage: false);
        var response = await client.PostAsync($"/admin/products/edit/{productId}", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("greater than zero");
    }

    [Fact]
    public async Task EditProduct_WithoutExistingImage_RequiresUpload()
    {
        var client = await OwnerBrowserAsync();
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "Imageless Product", 900m);

        // The edit form flags the missing image.
        var editPage = await client.GetAsync($"/admin/products/edit/{productId}");
        (await editPage.Content.ReadAsStringAsync()).Should().Contain("This product has no image yet");

        // Saving without an upload is rejected.
        var form = await BuildProductFormAsync(client, $"/admin/products/edit/{productId}", "Imageless Product",
            await GetCategoryIdAsync(productId), 950m, includeImage: false);
        var response = await client.PostAsync($"/admin/products/edit/{productId}", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Upload at least one product image");
    }

    [Fact]
    public async Task EditProduct_WithExistingImage_AllowsSaveWithoutNewUpload()
    {
        var client = await OwnerBrowserAsync();
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "Has Image Product", 800m);
        await AttachImagesAsync(productId, 1);

        var form = await BuildProductFormAsync(client, $"/admin/products/edit/{productId}", "Has Image Product",
            await GetCategoryIdAsync(productId), 850m, includeImage: false);
        var response = await client.PostAsync($"/admin/products/edit/{productId}", form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "an upload is optional while the product keeps an image");
    }

    [Fact]
    public async Task RemoveLastImage_IsBlocked_AndImageRemains()
    {
        var client = await OwnerBrowserAsync();
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "One Image Product", 700m);
        var imageId = (await AttachImagesAsync(productId, 1))[0];

        var token = TestSeed.ExtractAntiforgeryToken(
            await (await client.GetAsync($"/admin/products/{productId}")).Content.ReadAsStringAsync());
        var response = await client.PostAsync($"/admin/products/images/{productId}/remove/{imageId}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var details = await client.GetAsync(response.Headers.Location!.ToString());
        (await details.Content.ReadAsStringAsync()).Should().Contain("must keep at least one image");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var product = await db.Products.Include(p => p.Images).FirstAsync(p => p.Id == productId);
        product.Images.Should().HaveCount(1, "the last image must not have been removed");
    }

    [Fact]
    public async Task RemoveOneOfManyImages_Succeeds()
    {
        var client = await OwnerBrowserAsync();
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "Two Image Product", 750m);
        var imageIds = await AttachImagesAsync(productId, 2);

        var token = TestSeed.ExtractAntiforgeryToken(
            await (await client.GetAsync($"/admin/products/{productId}")).Content.ReadAsStringAsync());
        var response = await client.PostAsync($"/admin/products/images/{productId}/remove/{imageIds[0]}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var product = await db.Products.Include(p => p.Images).FirstAsync(p => p.Id == productId);
        product.Images.Should().HaveCount(1);
    }

    [Fact]
    public async Task EditProduct_WithChangedPrice_PersistsAndSurvivesReload()
    {
        // Core regression for "the edited product price is not taking effect after saving":
        // the FULL edit flow must persist the submitted price and every other editable
        // field, and the new values must still be there after the redirect AND a fresh
        // page load (refresh) — on the admin details page, the edit form, and the DB row.
        var client = await OwnerBrowserAsync();
        const decimal newPrice = 3456.78m;
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "Persistence Check Product", 1200m);
        await AttachImagesAsync(productId, 1); // a product must keep an image; the edit then needs no upload

        var form = await BuildProductFormAsync(client, $"/admin/products/edit/{productId}",
            "Persistence Check Product", await GetCategoryIdAsync(productId), newPrice, includeImage: false);
        var response = await client.PostAsync($"/admin/products/edit/{productId}", form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the edit must save and redirect, not error");

        // 1. The redirect target (admin details) shows the new price.
        var details = await client.GetAsync(response.Headers.Location!.ToString());
        var detailsHtml = await details.Content.ReadAsStringAsync();
        detailsHtml.Should().Contain("₦3,456.78");

        // 2. A FRESH page load (simulated refresh) still shows the new price in the form.
        var editPage = await client.GetAsync($"/admin/products/edit/{productId}");
        var editHtml = await editPage.Content.ReadAsStringAsync();
        editHtml.Should().Contain("value=\"3456.78\"");

        // 3. And the details page again after that (double refresh).
        (await client.GetStringAsync($"/admin/products/{productId}")).Should().Contain("₦3,456.78");

        // 4. The database row itself carries every submitted field.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var row = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        row.SellingPrice.Amount.Should().Be(newPrice);
        row.Description.Should().Be("Integration-test product");
    }

    [Fact]
    public async Task EditProduct_RenamingOntoAnotherProductsSlug_PersistsWithUniqueSlug()
    {
        // SQL Server enforces a UNIQUE index on Products.Slug ([IsDeleted] = 0). The old
        // update path regenerated the slug from the name on EVERY save with no collision
        // resolution, so renaming a product onto an existing slug aborted the WHOLE edit
        // in an unhandled DbUpdateException — the submitted price was silently lost. The
        // handler now resolves a unique slug up front and the edit persists.
        var client = await OwnerBrowserAsync();
        var productA = await TestSeed.SeedProductAsync(_factory.Services, "Bundle Deal", 1000m);
        var productB = await TestSeed.SeedProductAsync(_factory.Services, "Big Deal", 2000m);
        await AttachImagesAsync(productA, 1);

        var form = await BuildProductFormAsync(client, $"/admin/products/edit/{productA}",
            "Big Deal", await GetCategoryIdAsync(productA), 4321m, includeImage: false);
        var response = await client.PostAsync($"/admin/products/edit/{productA}", form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a colliding name must never abort the edit with a 500");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var a = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productA);
        var b = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productB);

        a.Slug.Value.Should().NotBe(b.Slug.Value, "both products must stay addressable");
        a.Slug.Value.Should().StartWith("big-deal", "the collision is resolved with a -2/-3 suffix");
        a.Name.Should().Be("Big Deal");
        a.SellingPrice.Amount.Should().Be(4321m, "the price edit must persist together with the rename");
        b.Slug.Value.Should().Be("big-deal");
        b.SellingPrice.Amount.Should().Be(2000m, "the other product is untouched");

        // The details page reflects the edited product everywhere it is displayed.
        var detailsHtml = await client.GetStringAsync($"/admin/products/{productA}");
        detailsHtml.Should().Contain("Big Deal").And.Contain("₦4,321.00");
    }

    [Fact]
    public async Task CreateProduct_WithDuplicateName_SucceedsWithUniqueSlug()
    {
        // Same hole on the create path: a second product with an identical name would hit
        // the unique Slug index on SQL Server and 500. Both creates must succeed with
        // distinct slugs.
        var client = await OwnerBrowserAsync();
        var categoryId = await SeedCategoryAsync("Dup Name Cat");

        var form1 = await BuildProductFormAsync(client, "/admin/products/create", "Duplicate Name Test", categoryId, 500m, includeImage: true);
        var response1 = await client.PostAsync("/admin/products/create", form1);
        response1.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var form2 = await BuildProductFormAsync(client, "/admin/products/create", "Duplicate Name Test", categoryId, 750m, includeImage: true);
        var response2 = await client.PostAsync("/admin/products/create", form2);
        response2.StatusCode.Should().Be(HttpStatusCode.Redirect, "the second same-named product must be creatable");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var slugs = await db.Products.AsNoTracking()
            .Where(p => p.Name == "Duplicate Name Test")
            .Select(p => new { p.SellingPrice.Amount, p.Slug.Value })
            .ToListAsync();
        slugs.Should().HaveCount(2);
        slugs.Select(x => x.Value).Should().OnlyHaveUniqueItems();
        slugs.Should().Contain(x => x.Amount == 500m).And.Contain(x => x.Amount == 750m);
    }

    [Fact]
    public async Task Storefront_ProductWithoutImage_ShowsNoImageTile_NotHero()
    {
        // Seed an image-less product directly (pre-rule legacy data) and confirm the
        // storefront renders the neutral tile instead of the hero banner.
        var productId = await TestSeed.SeedProductAsync(_factory.Services, "Legacy Imageless", 500m);
        var client = CreateBrowser();

        var response = await client.GetAsync($"/products/{productId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("/img/hero-products.jpg");
        body.Should().Contain("No image");
    }

    private async Task<Guid> GetCategoryIdAsync(Guid productId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return (await db.Products.AsNoTracking().FirstAsync(p => p.Id == productId)).CategoryId;
    }

    /// <summary>Attaches n images to a product directly in the database; returns their ids.</summary>
    private async Task<List<Guid>> AttachImagesAsync(Guid productId, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var product = await db.Products.Include(p => p.Images).FirstAsync(p => p.Id == productId);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
            ids.Add(product.AddImage($"/uploads/productimage/test-{Guid.NewGuid():n}.png", null, i == 0).Id);
        await db.SaveChangesAsync();
        return ids;
    }
}
