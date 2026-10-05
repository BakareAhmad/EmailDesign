using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>
/// Marketing unsubscribe landing (the link at the bottom of every marketing email).
/// The opaque per-customer token identifies the subscriber - no login required.
/// Transactional emails (orders, receipts, account security) are unaffected: this only
/// opts the customer out of MARKETING broadcasts.
/// </summary>
[AllowAnonymous]
[Route("unsubscribe")]
public sealed class UnsubscribeController(
    ICustomerRepository customers,
    IUnitOfWork uow) : MvcControllerBase
{
    [HttpGet("{token:guid}")]
    public async Task<IActionResult> Index(Guid token)
    {
        var customer = await customers.GetByUnsubscribeTokenAsync(token);
        if (customer is null)
            return View("Unsubscribe", (false, true)); // invalid link

        return View("Unsubscribe", (customer.MarketingSubscribed, false));
    }

    [HttpPost("{token:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(Guid token)
    {
        var customer = await customers.GetByUnsubscribeTokenAsync(token);
        if (customer is null)
            return View("Unsubscribe", (false, true));

        customer.UnsubscribeFromMarketing();
        customers.Update(customer);
        await uow.SaveChangesAsync();
        return View("Unsubscribe", (false, false)); // (stillSubscribed, invalidLink)
    }
}
