using System.Net;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Category management rules (owner directive 2026-09-09):
///  - display order is automatic: the form has no order field, new categories are
///    appended at the end, and edits keep the current position;
///  - the admin categories page supports search by name/description.
/// </summary>
public class CategoryManagementTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CategoryManagementTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> OwnerBrowserAsync() =>
        await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

    private async Task<Guid> SeedCategoryAsync(string name, int displayOrder, string? description = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create(name, description, displayOrder);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    private async Task<int> GetOrderAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return (await db.Categories.AsNoTracking().FirstAsync(c => c.Id == id)).DisplayOrder;
    }

    private async Task<int> GetMaxOrderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return await db.Categories.AsNoTracking().MaxAsync(c => (int?)c.DisplayOrder) ?? 0;
    }

    private async Task<HttpResponseMessage> PostCategoryFormAsync(
        HttpClient client, string url, string name, string? description = null)
    {
        var page = await client.GetAsync(url.Contains("/edit/") ? url : "/admin/categories/create");
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Description"] = description ?? string.Empty,
            ["IsActive"] = "true",
            ["__RequestVerificationToken"] = token
        }));
    }

    [Fact]
    public async Task CreateCategory_FormHasNoOrderField_AndAppendsAtEnd()
    {
        var client = await OwnerBrowserAsync();
        await SeedCategoryAsync("Order Base A", 5);
        await SeedCategoryAsync("Order Base B", 9);

        // The create form no longer exposes a manual order input.
        var form = await client.GetAsync("/admin/categories/create");
        var formHtml = await form.Content.ReadAsStringAsync();
        formHtml.Should().NotContain("name=\"DisplayOrder\"");
        formHtml.Should().Contain("Ordering is automatic");

        // Sibling tests share this fixture's database, so assert relative to the
        // current maximum instead of absolute values.
        var maxBefore = await GetMaxOrderAsync();

        // Creating appends after the highest existing order, each after the previous.
        var first = await PostCategoryFormAsync(client, "/admin/categories/create", "Appended Cat One");
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var second = await PostCategoryFormAsync(client, "/admin/categories/create", "Appended Cat Two");
        second.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var one = await db.Categories.AsNoTracking().FirstAsync(c => c.Name == "Appended Cat One");
        var two = await db.Categories.AsNoTracking().FirstAsync(c => c.Name == "Appended Cat Two");
        one.DisplayOrder.Should().Be(maxBefore + 1, "new categories go to the last spot");
        two.DisplayOrder.Should().Be(maxBefore + 2, "each new category goes after the previous one");
        one.DisplayOrder.Should().BeGreaterThan(9, "the append must land after the seeded orders");
    }

    [Fact]
    public async Task EditCategory_KeepsCurrentPosition()
    {
        var client = await OwnerBrowserAsync();
        var id = await SeedCategoryAsync("Positioned Cat", 3);

        var response = await PostCategoryFormAsync(client, $"/admin/categories/edit/{id}", "Positioned Cat Renamed");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await GetOrderAsync(id)).Should().Be(3, "editing must not move the category");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.Categories.AsNoTracking().FirstAsync(c => c.Id == id)).Name
            .Should().Be("Positioned Cat Renamed");
    }

    [Fact]
    public async Task Index_Search_FiltersByNameAndDescription()
    {
        var client = await OwnerBrowserAsync();
        await SeedCategoryAsync("Searchable Mugs", 100, "printed ceramics");
        await SeedCategoryAsync("Searchable Jerseys", 101, "sports apparel");
        await SeedCategoryAsync("Searchable Engraving", 102, "gifts described as ceramic-like");

        var byName = await client.GetAsync("/admin/categories?search=jerseys");
        var nameBody = await byName.Content.ReadAsStringAsync();
        nameBody.Should().Contain("Searchable Jerseys");
        nameBody.Should().NotContain("Searchable Mugs");

        var byDescription = await client.GetAsync("/admin/categories?search=ceramics");
        var descBody = await byDescription.Content.ReadAsStringAsync();
        descBody.Should().Contain("Searchable Mugs");
        descBody.Should().NotContain("Searchable Jerseys");

        var noMatch = await client.GetAsync("/admin/categories?search=zzz-nothing");
        var noMatchBody = await noMatch.Content.ReadAsStringAsync();
        noMatchBody.Should().Contain("No categories match");
        noMatchBody.Should().Contain("Clear search");
    }

    [Fact]
    public async Task Index_WithoutSearch_ShowsAllCategories()
    {
        var client = await OwnerBrowserAsync();
        await SeedCategoryAsync("Unfiltered Cat", 103);

        var response = await client.GetAsync("/admin/categories");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Unfiltered Cat");
        body.Should().Contain("Search categories...");
    }
}
