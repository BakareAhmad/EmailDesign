using System.Text;

namespace DeChimex.Host.Middleware;

/// <summary>
/// Renders the styled HTML error page used for browser (MVC) requests. Shared by the
/// <see cref="ExceptionHandlingMiddleware"/>, the rate-limiter rejection handler and any
/// other pipeline component that must turn an error status into a real page, so a failure
/// can never leave the browser staring at a blank document. Self-contained markup (no
/// layout, no external assets) so it renders even when the failure is unrelated to the
/// normal page pipeline.
/// </summary>
public static class ErrorPageWriter
{
    /// <summary>
    /// True when the request expects an HTML document: anything outside /api that accepts
    /// text/html (or sends no Accept header at all). Used to decide between the HTML error
    /// page and the JSON error envelope without duplicating the rule at every call site.
    /// </summary>
    public static bool IsBrowserHtmlRequest(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        var accept = context.Request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase)
            || accept.Length == 0;
    }

    public static string TitleFor(int status) => status switch
    {
        400 => "Please check your input",
        401 => "Sign in required",
        403 => "You do not have access",
        404 => "Not found",
        409 => "Conflict",
        422 => "Action not allowed",
        429 => "Too many requests",
        _ => "We ran into a problem"
    };

    /// <summary>
    /// The signed-in user's own home, for error-page links: business staff -> admin
    /// dashboard, developer-only accounts -> Developer Portal, everyone else (customers)
    /// -> their account. Never offers a portal the user cannot open. Mirrors the
    /// audience rules of _CustomerLayout and the post-login landing in AccountController.
    /// </summary>
    public static (string Href, string Label) AccountHomeFor(System.Security.Claims.ClaimsPrincipal user)
    {
        if (DeChimex.Shared.Constants.RoleNames.StaffRoles().Any(user.IsInRole))
            return ("/admin/dashboard", "Admin dashboard");
        if (user.IsInRole(DeChimex.Shared.Constants.RoleNames.Developer))
            return ("/dev", "Developer Portal");
        return ("/portal", "My account");
    }

    /// <summary>
    /// Writes the full error document. <paramref name="retryHref"/>, when set, adds a
    /// "Try again" button (used for transient errors such as 429 where repeating the
    /// same navigation shortly afterwards is the recovery path).
    /// </summary>
    public static async Task WriteAsync(HttpContext context, int status, string message, string traceId,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null, string? retryHref = null)
    {
        var authenticated = context.User?.Identity?.IsAuthenticated == true;
        var title = TitleFor(status);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'><link rel='icon' href='/favicon.ico' sizes='any'>");
        sb.Append("<meta name='viewport' content='width=device-width,initial-scale=1'>");
        sb.Append($"<title>{status} - {Escape(title)}</title><style>");
        sb.Append("body{margin:0;font-family:'Segoe UI',system-ui,-apple-system,sans-serif;background:#F8FAFC;color:#0F172A;display:flex;align-items:center;justify-content:center;min-height:100vh}");
        sb.Append(".card{background:#fff;border:1px solid #E2E8F0;border-radius:16px;box-shadow:0 10px 30px rgba(15,23,42,.06);max-width:560px;width:calc(100% - 48px);padding:40px 36px;text-align:center}");
        sb.Append(".code{font-size:52px;font-weight:800;letter-spacing:-.02em;color:#4F46E5}");
        sb.Append("h1{font-size:20px;margin:10px 0 8px}");
        sb.Append("p{color:#475569;font-size:14.5px;line-height:1.55;margin:0 0 18px}");
        sb.Append("ul{list-style:none;padding:0;margin:0 0 18px;text-align:left;display:grid;gap:6px}");
        sb.Append("li{background:#FEF2F2;border:1px solid #FECACA;color:#991B1B;border-radius:8px;padding:12px 14px;font-size:13px}");
        sb.Append(".links{display:flex;gap:10px;justify-content:center;flex-wrap:wrap}");
        sb.Append("a.btn{display:inline-block;padding:10px 18px;border-radius:10px;font-size:14px;font-weight:600;text-decoration:none}");
        sb.Append("a.primary{background:#4F46E5;color:#fff}a.ghost{background:#F1F5F9;color:#0F172A}");
        sb.Append(".trace{margin-top:22px;font-size:11px;color:#94A3B8}");
        sb.Append("</style></head><body><div class='card'>");
        sb.Append($"<div class='code'>{status}</div>");
        sb.Append($"<h1>{Escape(title)}</h1>");
        sb.Append($"<p>{Escape(message)}</p>");
        if (fieldErrors is { Count: > 0 })
        {
            sb.Append("<ul>");
            foreach (var (_, messages) in fieldErrors)
            {
                foreach (var m in messages)
                    sb.Append($"<li>{Escape(m)}</li>");
            }
            sb.Append("</ul>");
        }
        sb.Append("<div class='links'>");
        if (!string.IsNullOrEmpty(retryHref))
            sb.Append($"<a class='btn primary' href='{Escape(retryHref)}'>Try again</a>");
        else
            sb.Append("<a class='btn primary' href='/'>Back to home</a>");
        if (authenticated)
        {
            var (homeHref, homeLabel) = AccountHomeFor(context.User!);
            sb.Append($"<a class='btn ghost' href='{Escape(homeHref)}'>{Escape(homeLabel)}</a>");
        }
        else
            sb.Append("<a class='btn ghost' href='/Account/Login'>Sign in</a>");
        sb.Append("</div>");
        sb.Append($"<div class='trace'>Reference: {Escape(traceId)}</div>");
        sb.Append("</div></body></html>");

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(sb.ToString());
    }

    public static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;").Replace("\"", "&quot;");
}
