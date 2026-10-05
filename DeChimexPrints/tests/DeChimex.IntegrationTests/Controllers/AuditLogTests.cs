using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// GAP-01: verifies the AuditLog WRITE side. The AuditLogs entity/table/writer were already
/// registered and the read side (AdminReportsController.Staff) existed, but nothing wrote
/// audit rows, so the table was always empty. AuditableSoftDeleteInterceptor now records a
/// create/update/delete row per aggregate change within the same SaveChanges transaction.
/// </summary>
public class AuditLogTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuditLogTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static readonly string OwnerEmail = "owner@dechimexprinthub.com";

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = CreateBrowser();
        foreach (var password in new[] { "ChangeMe123!", "Owner!Pass9" })
        {
            var loginPage = await client.GetAsync("/Account/Login");
            var html = await loginPage.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "value=\"([^\"]+)\"").Groups[1].Value;

            var resp = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Email"] = OwnerEmail,
                    ["Password"] = password,
                    ["__RequestVerificationToken"] = token
                }));
            if (resp.StatusCode == HttpStatusCode.Redirect)
            {
                var location = resp.Headers.Location?.ToString() ?? "";
                if (location.Contains("/admin/security/password"))
                {
                    var pwdPage = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
                    var pwdToken = Regex.Match(pwdPage, "value=\"([^\"]+)\"").Groups[1].Value;
                    await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["CurrentPassword"] = password,
                            ["NewPassword"] = "Owner!Pass9",
                            ["ConfirmPassword"] = "Owner!Pass9",
                            ["__RequestVerificationToken"] = pwdToken
                        }));
                }
                return client;
            }
        }
        throw new InvalidOperationException("Owner login failed in AuditLogTests.");
    }

    private static async Task<string> GetAntiForgeryTokenAsync(HttpClient client, string url)
    {
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        return Regex.Match(tag, "value=\"([^\"]+)\"").Groups[1].Value;
    }

    private async Task<List<Row>> AuditRowsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        return await db.AuditLogs.AsNoTracking()
            .Select(a => new Row(a.Action, a.EntityName, a.EntityId, a.PerformedBy, a.OldValues, a.NewValues))
            .ToListAsync();
    }

    [Fact]
    public async Task CreateUpdateDeleteCategory_WritesAuditRows()
    {
        var client = await LoginAsOwnerAsync();
        var unique = "AuditTest_" + Guid.NewGuid().ToString("N")[..8];

        // --- CREATE ---
        var createToken = await GetAntiForgeryTokenAsync(client, "/admin/categories/create");
        var createResp = await client.PostAsync("/admin/categories/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Name"] = unique,
                ["Description"] = "audit test",
                ["DisplayOrder"] = "1",
                ["__RequestVerificationToken"] = createToken
            }));
        createResp.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "creating a category should redirect to the index");

        // --- EDIT ---
        Guid id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var cat = await db.Categories.SingleAsync(c => c.Name == unique);
            id = cat.Id;
        }
        var editToken = await GetAntiForgeryTokenAsync(client, $"/admin/categories/edit/{id}");
        var editResp = await client.PostAsync($"/admin/categories/edit/{id}", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Name"] = unique,
                ["Description"] = "audit test edited",
                ["DisplayOrder"] = "2",
                ["__RequestVerificationToken"] = editToken
            }));
        editResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "editing a category should redirect");

        // --- DELETE --- (form on the create page always renders the antiforgery token)
        var deleteToken = await GetAntiForgeryTokenAsync(client, "/admin/categories/create");
        var deleteResp = await client.PostAsync($"/admin/categories/delete/{id}", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = deleteToken
            }));
        deleteResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "deleting a category should redirect");

        // --- ASSERT: exactly one audit row per action for our category, attributed to owner ---
        var rows = (await AuditRowsAsync()).Where(r => r.EntityId == id.ToString()).ToList();

        rows.Select(r => r.Action).Should().Contain(new[] { "create", "update", "delete" });
        rows.Should().OnlyContain(r => r.PerformedBy == OwnerEmail);
        rows.Should().OnlyContain(r => r.EntityName == "Category");

        var create = rows.Single(r => r.Action == "create");
        create.NewValues.Should().Contain(unique);

        var update = rows.Single(r => r.Action == "update");
        update.OldValues.Should().BeNull("free-text descriptions are deliberately excluded from snapshots");
        update.NewValues.Should().BeNull("the description-only edit has no allowlisted scalar diff");

        var delete = rows.Single(r => r.Action == "delete");
        delete.OldValues.Should().Contain(unique);
    }

    private sealed record Row(string Action, string EntityName, string? EntityId, string? PerformedBy, string? OldValues, string? NewValues);
}
