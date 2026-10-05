using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Application.Common.Interfaces;
using FluentAssertions;
using DeChimex.Application.Features.Auth.Commands;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using DeChimex.Persistence.Context;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeChimex.IntegrationTests;

/// <summary>
/// Shared setup helpers for the integration suite. The JSON /api/v1/auth/register + login
/// endpoints that previously seeded test users have been removed (they served the retired
/// SPA; MVC uses cookie auth). These helpers seed state through the SAME Application layer
/// the MVC controllers use, and authenticate over the MVC cookie login page.
/// </summary>
public static class TestSeed
{
    public const string DefaultPassword = "Passw0rd!Strong";

    /// <summary>Registers a customer through the Application RegisterCommand (creates the login
    /// user, a linked customer profile, assigns the Customer role, and issues tokens). Returns the
    /// created user id.</summary>
    public static async Task<Guid> RegisterCustomerAsync(
        IServiceProvider services, string email, string password = DefaultPassword,
        string firstName = "Test", string lastName = "Customer")
    {
        using var scope = services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var auth = await sender.Send(new RegisterCommand(
            new RegisterRequest(firstName, lastName, email, "+2348000000002", password),
            "127.0.0.1"));
        return auth.UserId;
    }

    /// <summary>Reads the issued email-verification token (what the Brevo email carries).</summary>
    public static async Task<string?> GetVerificationTokenAsync(IServiceProvider services, string email)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await users.GetByEmailAsync(email);
        return user?.EmailVerificationToken;
    }

    /// <summary>Completes gated signup for tests: submits the issued token, as the user would
    /// after receiving the Brevo verification email. Required before MVC login accepts the account.</summary>
    public static async Task VerifyCustomerAsync(IServiceProvider services, string email)
    {
        using var scope = services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var token = await GetVerificationTokenAsync(services, email);
        token.Should().NotBeNullOrWhiteSpace("registration should issue a verification token");
        await sender.Send(new VerifyEmailCommand(new VerifyEmailRequest(email, token!)));
    }

    /// <summary>Seeds an active, visible catalog product (plus a category) directly, so
    /// storefront tests have a product to browse without the removed /api/v1/products.</summary>
    public static async Task<Guid> SeedProductAsync(
        IServiceProvider services, string name, decimal price = 123.45m)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();

        var category = Category.Create($"{name} Category", "test seed category", 1);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var product = Product.Create(
            name,
            "SKU-" + Guid.NewGuid().ToString("N")[..12],
            "Seeded product for integration tests",
            category.Id,
            price);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product.Id;
    }

    /// <summary>Creates a staff login user with the given role (Owner/Manager/SalesStaff/
    /// InventoryStaff/Designer). Used where tests previously created staff via the removed
    /// /api/v1/users endpoint.</summary>
    public static async Task CreateStaffUserAsync(
        IServiceProvider services, string email, string password, string roleName,
        string firstName = "Scope", string lastName = "Test", string? username = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var role = await db.Roles.FirstAsync(r => r.NormalizedName == roleName.ToUpperInvariant());
        var user = User.CreateStaff(email, hasher.Hash(password), firstName, lastName,
            username ?? Username.SuggestFromEmail(email));
        user.AssignRole(role.Id);
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    /// <summary>Authenticates an HTTP client over the MVC cookie login form (with antiforgery).
    /// Returns true when the login redirected (successful credential exchange).</summary>
    public static async Task<bool> LoginMvcAsync(HttpClient client, string email, string password)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        if (!loginPage.IsSuccessStatusCode)
            return false;

        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(html);
        if (string.IsNullOrEmpty(token))
            return false;

        var post = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = password,
                ["__RequestVerificationToken"] = token
            }));
        return post.StatusCode == HttpStatusCode.Redirect;
    }

    public static string ExtractAntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>Owner bootstrap credentials used across the suite (the seeder flags the owner
    /// with a forced password change on first login; the test completes it, changing the password
    /// to <see cref="OwnerChangedPassword"/>).</summary>
    public const string OwnerEmail = "owner@dechimexprinthub.com";
    public const string OwnerBootstrapPassword = "ChangeMe123!";
    public const string OwnerChangedPassword = "Owner!Pass9";

    /// <summary>Logs in as the seeded Owner over the MVC cookie flow, completing the forced
    /// password change when it is triggered. Returns a browser authenticated as the owner; the
    /// owner password is updated to <see cref="OwnerChangedPassword"/> once the change completes.</summary>
    public static async Task<HttpClient> LoginOwnerMvcAsync(
        IServiceProvider services, Func<HttpClient> createBrowser)
    {
        foreach (var password in new[] { OwnerBootstrapPassword, OwnerChangedPassword })
        {
            var browser = createBrowser();
            var loginPage = await browser.GetAsync("/Account/Login");
            var token = ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
            var post = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Email"] = OwnerEmail,
                    ["Password"] = password,
                    ["__RequestVerificationToken"] = token
                }));
            if (post.StatusCode != HttpStatusCode.Redirect)
                continue;

            var location = post.Headers.Location?.ToString() ?? "";
            if (location.Contains("/admin/security/password"))
                await CompleteForcedPasswordChangeAsync(browser, password);
            return browser;
        }
        throw new InvalidOperationException("Owner login failed with both bootstrap and changed passwords.");
    }

    private static async Task CompleteForcedPasswordChangeAsync(HttpClient browser, string currentPassword)
    {
        var page = await browser.GetAsync("/admin/security/password");
        var token = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        var response = await browser.PostAsync("/admin/security/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["CurrentPassword"] = currentPassword,
                ["NewPassword"] = OwnerChangedPassword,
                ["ConfirmPassword"] = OwnerChangedPassword,
                ["__RequestVerificationToken"] = token
            }));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
    }
}
