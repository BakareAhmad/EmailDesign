using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Cart;
using DeChimex.Domain.Repositories;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>
/// Header cart badge driven by the server-side cart (the storefront has a single cart,
/// stored per customer; anonymous visitors see no badge).
/// </summary>
public sealed class CartBadgeViewComponent(ISender sender, ICustomerRepository customers) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var count = 0;
        var current = HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (current.UserId is Guid userId)
        {
            var customer = await customers.GetByUserIdAsync(userId, HttpContext.RequestAborted);
            if (customer is not null)
            {
                var cart = await sender.Send(new GetCartQuery(customer.Id), HttpContext.RequestAborted);
                count = cart?.ItemCount ?? 0;
            }
        }
        return View(count);
    }
}
