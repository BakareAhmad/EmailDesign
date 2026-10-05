using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DeChimex.Contracts.Auth;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Verifies the MVC account flows that were previously only reachable via the JSON API:
/// forgot password, reset password and email verification. These are live product flows
/// whose emailed links used to point at the retired vanilla-JS SPA pages; they now render
/// server-side MVC views.
/// </summary>
public class AuthFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static string ExtractToken(string html) =>
        Regex.Match(html, "name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"([^\\\"]+)\\\"").Groups[1].Value;

    private async Task RegisterCustomerAsync(string email)
        => await TestSeed.RegisterCustomerAsync(_factory.Services, email);

    private async Task<string> GetVerificationTokenAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Email.Value == email);
        return user.EmailVerificationToken!;
    }

    private async Task<string> GetResetTokenAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Email.Value == email);
        return user.PasswordResetToken!;
    }

    [Fact]
    public async Task ForgotPassword_Page_Renders()
    {
        var response = await CreateBrowser().GetAsync("/Account/ForgotPassword");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Forgot your password?");
    }

    [Fact]
    public async Task ForgotPassword_Submit_AlwaysShowsGenericSuccess()
    {
        var client = CreateBrowser();
        var page = await client.GetAsync("/Account/ForgotPassword");
        var token = ExtractToken(await page.Content.ReadAsStringAsync());

        // Unknown email: no enumeration, still shows the generic confirmation.
        var post = await client.PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = $"nobody-{Guid.NewGuid():N}@example.com",
                ["__RequestVerificationToken"] = token
            }));
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await post.Content.ReadAsStringAsync();
        body.Should().Contain("reset link has been sent");
    }

    [Fact]
    public async Task ResetPassword_WithInvalidToken_ShowsError()
    {
        // A real account but a wrong (never-issued) token, so the token check fires
        // rather than the account-not-found path.
        var email = $"badreset-{Guid.NewGuid():N}@example.com";
        await RegisterCustomerAsync(email);

        var client = CreateBrowser();
        var page = await client.GetAsync("/Account/ResetPassword");
        var token = ExtractToken(await page.Content.ReadAsStringAsync());

        var post = await client.PostAsync("/Account/ResetPassword", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Token"] = "not-a-real-token",
                ["NewPassword"] = "NewPassw0rd!",
                ["ConfirmPassword"] = "NewPassw0rd!",
                ["__RequestVerificationToken"] = token
            }));
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await post.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid reset token");
    }

    [Fact]
    public async Task ResetPassword_WithValidToken_RedirectsToLogin()
    {
        var email = $"reset-{Guid.NewGuid():N}@example.com";
        await RegisterCustomerAsync(email);
        await TestSeed.VerifyCustomerAsync(_factory.Services, email); // gated signup: verify so login is allowed

        // Trigger a reset request -> generates a token on the user.
        var client = CreateBrowser();
        var page = await client.GetAsync("/Account/ForgotPassword");
        var antiforgery = ExtractToken(await page.Content.ReadAsStringAsync());
        await client.PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["__RequestVerificationToken"] = antiforgery
            }));

        var resetToken = await GetResetTokenAsync(email);

        var resetPage = await client.GetAsync($"/Account/ResetPassword?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(resetToken)}");
        resetPage.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resetPage.Content.ReadAsStringAsync()).Should().Contain("Choose a new password");

        var resetPost = await client.PostAsync("/Account/ResetPassword", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Token"] = resetToken,
                ["NewPassword"] = "NewPassw0rd!",
                ["ConfirmPassword"] = "NewPassw0rd!",
                ["__RequestVerificationToken"] = ExtractToken(await resetPage.Content.ReadAsStringAsync())
            }));
        resetPost.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resetPost.Headers.Location!.OriginalString.Should().Contain("/Account/Login");

        // The new password now works.
        var login = await client.GetAsync("/Account/Login");
        var loginToken = ExtractToken(await login.Content.ReadAsStringAsync());
        var loginPost = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = "NewPassw0rd!",
                ["__RequestVerificationToken"] = loginToken
            }));
        loginPost.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task VerifyEmail_WithValidToken_Verifies()
    {
        var email = $"verify-{Guid.NewGuid():N}@example.com";
        await RegisterCustomerAsync(email);
        var verifyToken = await GetVerificationTokenAsync(email);

        var client = CreateBrowser();
        var response = await client.GetAsync($"/Account/VerifyEmail?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(verifyToken)}");
        // Gated signup completes here: token accepted -> signed in -> portal.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/portal");
        var portal = await client.GetAsync("/portal");
        portal.StatusCode.Should().Be(HttpStatusCode.OK, "the verified user should hold a session");
    }

    [Fact]
    public async Task VerifyEmail_WithInvalidToken_ShowsError()
    {
        // A real account with a never-issued token so the token check fires.
        var email = $"badverify-{Guid.NewGuid():N}@example.com";
        await RegisterCustomerAsync(email);

        var client = CreateBrowser();
        var page = await client.GetAsync("/Account/VerifyEmail");
        var antiforgery = ExtractToken(await page.Content.ReadAsStringAsync());

        var post = await client.PostAsync("/Account/VerifyEmail", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Token"] = "bad-token",
                ["__RequestVerificationToken"] = antiforgery
            }));
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        (await post.Content.ReadAsStringAsync()).Should().Contain("Invalid verification token");
    }

    [Fact]
    public async Task Register_DoesNotSignIn_AndRedirectsToVerify()
    {
        var email = $"gated-{Guid.NewGuid():N}@example.com";
        var client = CreateBrowser();
        var page = await client.GetAsync("/Account/Register");
        var antiforgery = ExtractToken(await page.Content.ReadAsStringAsync());
        var post = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["FirstName"] = "Gate",
                ["LastName"] = "Test",
                ["Email"] = email,
                ["Phone"] = "08012345678",
                ["Password"] = "Pass1234!",
                ["ConfirmPassword"] = "Pass1234!",
                ["__RequestVerificationToken"] = antiforgery
            }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain("/Account/VerifyEmail");

        // No session yet: the portal bounces straight back to login.
        var portal = await client.GetAsync("/portal");
        portal.StatusCode.Should().Be(HttpStatusCode.Redirect);
        portal.Headers.Location!.ToString().Should().Contain("/Account/Login");
    }

    [Fact]
    public async Task Login_BeforeVerification_IsBlocked()
    {
        var email = $"unverified-{Guid.NewGuid():N}@example.com";
        await RegisterCustomerAsync(email); // leaves the account unverified on purpose

        var client = CreateBrowser();
        var page = await client.GetAsync("/Account/Login");
        var antiforgery = ExtractToken(await page.Content.ReadAsStringAsync());
        var post = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = TestSeed.DefaultPassword,
                ["__RequestVerificationToken"] = antiforgery
            }));
        // The login form has no token field; an unverified login must land the user on the
        // verify page (token input + resend) rather than stranding them back on the login form.
        post.StatusCode.Should().Be(HttpStatusCode.Redirect, "unverified login should redirect to the verify page");
        var location = post.Headers.Location?.ToString();
        location.Should().Contain("/Account/VerifyEmail");
        // The redirect Location keeps '@' unescaped; Uri.EscapeDataString would render %40.
        location.Should().Contain($"email={email}");
        var verifyPage = await client.GetAsync(location!);
        verifyPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await verifyPage.Content.ReadAsStringAsync();
        html.Should().Contain("not verified", "the info banner should explain why they were redirected");
        html.Should().Contain("Verification token", "the token input must be available on this page");
        html.Should().Contain(email, "the email should be prefilled");
    }

    [Fact]
    public async Task VerifyEmail_SixDigitCode_Lockout_Then_Resend_NewCode_Works()
    {
        var email = $"otp-{Guid.NewGuid():N}@example.com";
        await RegisterCustomerAsync(email); // leaves the account unverified with a pending code
        var original = await GetVerificationTokenAsync(email);
        original.Should().MatchRegex("^[0-9]{6}$", "verification codes are 6-digit numeric OTPs");
        // A wrong code that can never collide with the real one:
        var wrong = original == "000000" ? "000001" : "000000";

        var client = CreateBrowser();
        var page = await client.GetAsync($"/Account/VerifyEmail?email={email}");
        var antiforgery = ExtractToken(await page.Content.ReadAsStringAsync());

        async Task<HttpResponseMessage> PostVerifyAsync(string token) => await client.PostAsync(
            "/Account/VerifyEmail",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Token"] = token,
                ["__RequestVerificationToken"] = antiforgery
            }));

        // 9 wrong codes: normal invalid-token error, counter climbing (persisted per request).
        for (var i = 1; i <= 9; i++)
        {
            var post = await PostVerifyAsync(wrong);
            post.StatusCode.Should().Be(HttpStatusCode.OK, "the form should be re-shown");
            (await post.Content.ReadAsStringAsync()).Should().Contain("Invalid verification token");
        }

        // 10th wrong code: locked out, code invalidated.
        var locked = await PostVerifyAsync(wrong);
        (await locked.Content.ReadAsStringAsync()).Should().Contain("Too many incorrect attempts");

        // Even the ORIGINAL correct code is dead now.
        var dead = await PostVerifyAsync(original);
        (await dead.Content.ReadAsStringAsync()).Should().Contain("Too many incorrect attempts");

        // Resend issues a fresh 6-digit code...
        var resend = await client.PostAsync("/Account/ResendVerification", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["email"] = email,
                ["__RequestVerificationToken"] = antiforgery
            }));
        resend.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var fresh = await GetVerificationTokenAsync(email);
        fresh.Should().MatchRegex("^[0-9]{6}$");
        fresh.Should().NotBe(original, "resend must issue a new code (and reset the attempt budget)");

        // ...and the fresh code completes signup straight into the portal.
        var verify = await PostVerifyAsync(fresh);
        verify.StatusCode.Should().Be(HttpStatusCode.Redirect, "successful verification signs the user in");
        verify.Headers.Location!.ToString().Should().Contain("/portal");
    }
}
