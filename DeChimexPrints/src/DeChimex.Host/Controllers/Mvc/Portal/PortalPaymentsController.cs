using DeChimex.Application.Features.Payments.Queries;
using DeChimex.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Portal;

/// <summary>Customer portal: payment history.</summary>
[Authorize(Roles = Shared.Constants.RoleNames.Customer)]
[Route("portal/payments")]
public sealed class PortalPaymentsController(ICustomerRepository customers) : PortalControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Payments";
        ViewData["Heading"] = "Payments";
        ViewData["ActiveNav"] = "payments";

        var customer = await ResolveCustomerAsync(customers, HttpContext.RequestAborted);
        var payments = await Sender.Send(new GetPaymentHistoryQuery(customer.Id));
        return View(payments);
    }
}
