using DeChimex.Application.Common.Exceptions;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>Base for portal controllers: resolves the current user's customer profile,
/// mirroring ApiControllerBase.RequireCustomerAsync for the JSON API.</summary>
public abstract class PortalControllerBase : MvcControllerBase
{
    protected async Task<Customer> ResolveCustomerAsync(ICustomerRepository customers, CancellationToken ct)
    {
        var customer = CurrentUser.UserId is null
            ? null
            : await customers.GetByUserIdAsync(CurrentUser.UserId.Value, ct);
        return customer ?? throw new NotFoundException("No customer profile is linked to this account.");
    }
}
