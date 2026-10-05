using DeChimex.Application.Common.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>Base for MVC controllers: exposes MediatR + current-user the same way
/// ApiControllerBase does for the JSON API.</summary>
public abstract class MvcControllerBase : Controller
{
    private ISender? _sender;
    protected ISender Sender => _sender ??= HttpContext.RequestServices.GetRequiredService<ISender>();

    private ICurrentUser? _currentUser;
    protected ICurrentUser CurrentUser => _currentUser ??= HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
}
