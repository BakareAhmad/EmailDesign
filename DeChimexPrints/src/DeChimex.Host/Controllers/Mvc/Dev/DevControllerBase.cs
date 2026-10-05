using DeChimex.Host.DevPortal;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>Declares the dev.* permission an action (or controller) requires in addition to dev.portal.view.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DevPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Base for every Developer Portal controller.
///  1. [Authorize(Policy = dev.portal.view)] — cookie claim gate (customers/normal staff never pass).
///  2. Live DB re-check of dev.portal.view AND the action's [DevPermission] on every request, so a
///     revoked Developer role / permission takes effect immediately (not at cookie expiry).
///  3. Responses are marked no-store (diagnostics must not sit in shared/browser caches).
/// The effective dev permission set is exposed to the layout (ViewBag.DevPerms) for navigation.
/// </summary>
[Authorize(Policy = Permissions.Developer.PortalView)]
public abstract class DevControllerBase : MvcControllerBase
{
    protected HashSet<string> DevPerms { get; private set; } = new();

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        if (CurrentUser.UserId is not Guid userId)
        {
            context.Result = Challenge();
            return;
        }

        var db = HttpContext.RequestServices.GetRequiredService<DeChimexDbContext>();
        try
        {
            DevPerms = await DevAccess.DevPermissionsAsync(db, userId, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.Result = ClientGone();
            return;
        }

        var required = context.ActionDescriptor.EndpointMetadata.OfType<DevPermissionAttribute>().Select(a => a.Permission)
            .Append(Permissions.Developer.PortalView).Distinct();
        if (required.Any(p => !DevPerms.Contains(p)))
        {
            context.Result = Forbid();
            return;
        }

        ViewBag.DevPerms = DevPerms;
        await next();
    }

    protected bool Can(string permission) => DevPerms.Contains(permission);

    /// <summary>
    /// Runs a read-only page/JSON action and turns "the browser went away" into a quiet 499.
    /// Every dev query is bound to HttpContext.RequestAborted, so when the browser drops the
    /// request (reload, second click on Sign in, navigating away mid-load, the 30s dashboard
    /// poll still in flight when the tab changes page) EF throws OperationCanceledException.
    /// The global middleware already maps that to 499, but the exception had to leave user code
    /// through MVC first — which the Visual Studio debugger reports as "Exception User-Unhandled"
    /// on every aborted dashboard load. Catching it here (only when the CLIENT aborted — a real
    /// timeout or any other cancellation still propagates) keeps the debugger and logs quiet.
    /// </summary>
    protected async Task<IActionResult> UnlessClientAborted(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return ClientGone();
        }
    }

    /// <summary>
    /// 499 "Client Closed Request" — nothing is written; nobody is listening. Status-code pages are
    /// switched off for this response, otherwise UseStatusCodePagesWithReExecute would re-run the
    /// whole request through /Error (re-executing the dev action) for a browser that has gone.
    /// </summary>
    private StatusCodeResult ClientGone()
    {
        if (HttpContext.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        return new StatusCodeResult(499);
    }

    /// <summary>Views live under Views/Dev.</summary>
    protected ViewResult DevView(string name, object? model = null) => View($"~/Views/Dev/{name}.cshtml", model);
}
