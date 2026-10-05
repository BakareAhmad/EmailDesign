using Microsoft.Extensions.Options;
using DeChimex.Infrastructure.Options;

namespace DeChimex.Host.Middleware;

/// <summary>Adds common security response headers to mitigate XSS, clickjacking and sniffing.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<PaystackOptions> paystackOptions)
    {
        var headers = context.Response.Headers;
        
        // Prevent MIME type sniffing
        headers["X-Content-Type-Options"] = "nosniff";
        
        // Prevent clickjacking
        headers["X-Frame-Options"] = "DENY";
        
        // Control referrer information
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        
        // Disable XSS filter (modern browsers use CSP instead)
        headers["X-XSS-Protection"] = "0";
        
        // Restrict browser features
        // Microphone is self-allowed (workspace voice notes); camera stays fully blocked.
        headers["Permissions-Policy"] = "geolocation=(), microphone=(self), camera=(), payment=()";
        
        // Paystack hosted checkout: the pay POST 302-redirects cross-origin to the
        // gateway, and Chrome/Edge enforce form-action across that redirect chain,
        // so the gateway host must be allowlisted or the browser refuses to leave
        // for checkout (see AI_HANDOFF §78-§79). The allowlist follows the CONFIGURED
        // Paystack:ApiBaseUrl origin so a mock/self-hosted gateway works in tests
        // without widening anything in production (api.paystack.co sends no forms).
        var gatewayOrigins = new List<string> { "form-action 'self'", "https://checkout.paystack.com" };
        try
        {
            var apiOrigin = new Uri(paystackOptions.Value.ApiBaseUrl).GetLeftPart(UriPartial.Authority);
            if (!string.IsNullOrWhiteSpace(apiOrigin) && !gatewayOrigins.Contains(apiOrigin))
                gatewayOrigins.Add(apiOrigin);
        }
        catch (UriFormatException)
        {
            // Misconfigured base URL: keep the static allowlist rather than breaking every response.
        }

        // Content Security Policy that restricts resource loading
        var csp = string.Join("; ", new[]
        {
            "default-src 'self'",
            // AUD-022: no 'unsafe-eval' (nothing in the app evals) and no Tailwind CDN origin — the
            // app ships compiled CSS (wwwroot/css/tailwind.build.css), the CDN entry was dead weight.
            "script-src 'self' 'unsafe-inline' https://js.paystack.co",
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
            "font-src 'self' https://fonts.gstatic.com",
            "img-src 'self' data: https: blob:",
            "connect-src 'self' https://api.paystack.co",
            "frame-src 'self' https://checkout.paystack.com",
            "object-src 'none'",
            "base-uri 'self'",
            string.Join(" ", gatewayOrigins)
        });
        headers["Content-Security-Policy"] = csp;
        
        // Prevent DNS prefetching for privacy
        headers["X-DNS-Prefetch-Control"] = "off";
        
        // Remove server header information
        headers.Remove("Server");
        headers.Remove("X-Powered-By");
        
        await next(context);
    }
}
