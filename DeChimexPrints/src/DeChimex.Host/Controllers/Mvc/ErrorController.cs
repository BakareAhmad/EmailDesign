using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>
/// User-friendly error pages for the MVC surface. Both routes are reached through the
/// hand-rolled status-code-pages net in Program.cs (404 -> /Error/NotFound, any other
/// empty-body 4xx/5xx browser request -> /Error?code=N); /api paths keep their JSON
/// responses so the API contract is unchanged. Unhandled exceptions are rendered by
/// ExceptionHandlingMiddleware.
/// </summary>
public sealed class ErrorController : Controller
{
    [Route("/Error/NotFound")]
    [HttpGet]
    public IActionResult NotFound(string? original)
    {
        Response.StatusCode = StatusCodes.Status404NotFound;

        // When reached via status-code re-execution the original path is not in the query;
        // the framework exposes it through IStatusCodeReExecuteFeature.
        var reExecuted = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var originalPath = reExecuted?.OriginalPath ?? original;

        if (originalPath is not null && originalPath.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return Content(
                "{\"success\":false,\"message\":\"Not found.\",\"errorCode\":\"not.found\"}",
                "application/json");
        }

        return View("Error", new ErrorViewModel(404, "Page not found",
            "The page you are looking for does not exist or may have been moved."));
    }

    [Route("/Error")]
    [HttpGet]
    public IActionResult Error(int? code = null)
    {
        var status = code is >= 400 and < 600 ? code!.Value : StatusCodes.Status500InternalServerError;
        Response.StatusCode = status;
        return View(new ErrorViewModel(
            status,
            status == 404 ? "Page not found" : Middleware.ErrorPageWriter.TitleFor(status),
            status == 404
                ? "The page you are looking for does not exist or may have been moved."
                : "An unexpected error occurred. Please try again, and contact support if it persists."));
    }
}

public sealed record ErrorViewModel(int Code, string Title, string Message);
