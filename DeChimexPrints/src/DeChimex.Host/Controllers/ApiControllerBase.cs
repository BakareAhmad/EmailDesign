using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Contracts.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers;

/// <summary>Base controller providing MediatR access and standard response helpers.</summary>
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    private ISender? _sender;
    protected ISender Sender => _sender ??= HttpContext.RequestServices.GetRequiredService<ISender>();

    private ICurrentUser? _currentUser;
    protected ICurrentUser CurrentUser => _currentUser ??= HttpContext.RequestServices.GetRequiredService<ICurrentUser>();

    protected IActionResult Success<T>(T data, string? message = null) => Ok(ApiResponse<T>.Ok(data, message));
    protected IActionResult SuccessMessage(string message) => Ok(ApiResponse.Ok(message));

    protected IActionResult Paged<T>(PagedResult<T> result, string? message = null)
        => Ok(ApiResponse<PagedResponse<T>>.Ok(new PagedResponse<T>
        {
            Items = result.Items,
            Page = result.Page,
            PageSize = result.PageSize,
            TotalRecords = result.TotalRecords,
            TotalPages = result.TotalPages,
            HasPrevious = result.HasPrevious,
            HasNext = result.HasNext
        }, message));

    protected IActionResult MaybeFound<T>(T? data) where T : class
        => data is null ? NotFound(ApiResponse<T>.Fail("Resource not found.", "resource.notfound"))
                        : Ok(ApiResponse<T>.Ok(data));

    /// <summary>
    /// LOW-002: shared helper that resolves the authenticated user's customer profile (extracted
    /// from logic previously duplicated across the Cart, Wishlist and Payments controllers).
    /// Throws 404 if the account has no linked customer profile.
    /// </summary>
    protected async Task<DeChimex.Domain.Entities.Customer> RequireCustomerAsync(
        DeChimex.Domain.Repositories.ICustomerRepository customers, CancellationToken ct)
    {
        var customer = CurrentUser.UserId is null
            ? null
            : await customers.GetByUserIdAsync(CurrentUser.UserId.Value, ct);
        return customer ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException(
            "No customer profile is linked to this account.");
    }
}
