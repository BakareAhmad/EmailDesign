using Asp.Versioning;
using DeChimex.Application.Features.Payments.Commands;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers;

[ApiVersion("1.0")]
[AllowAnonymous]
public sealed class PaymentsController : ApiControllerBase
{
    /// <summary>
    /// Paystack webhook. Signature is verified before any state change; safe for retries.
    /// This is the ONLY remaining API surface for Payments because Paystack posts here
    /// server-to-server (it cannot be served via the MVC UI). All user-facing payment flows
    /// (initialize/verify/callback/history/invoice) run through the MVC Store/Checkout and
    /// Admin controllers.
    /// </summary>
    [HttpPost("webhook/paystack")]
    public async Task<IActionResult> PaystackWebhook(CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var signature = Request.Headers["x-paystack-signature"].ToString();
        await Sender.Send(new HandlePaystackWebhookCommand(body, signature), ct);
        return Ok(); // always 200 to acknowledge receipt
    }
}
