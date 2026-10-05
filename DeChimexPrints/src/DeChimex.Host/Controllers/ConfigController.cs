using Asp.Versioning;
using DeChimex.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DeChimex.Host.Controllers;

/// <summary>
/// HIGH-003: Exposes only safe, public-facing configuration (e.g. the Paystack PUBLIC key)
/// so the frontend can fetch it at runtime instead of hard-coding it. Secret keys are never
/// exposed here.
/// </summary>
[ApiVersion("1.0")]
[AllowAnonymous]
public sealed class ConfigController : ApiControllerBase
{
    private readonly PaystackOptions _paystackOptions;
    private readonly AppOptions _appOptions;

    public ConfigController(IOptions<PaystackOptions> paystackOptions, IOptions<AppOptions> appOptions)
    {
        _paystackOptions = paystackOptions.Value;
        _appOptions = appOptions.Value;
    }

    [HttpGet]
    public IActionResult GetPublicConfig()
    {
        var config = new
        {
            paystackPublicKey = _paystackOptions.PublicKey,
            currency = _appOptions.DefaultCurrency,
            supportEmail = _appOptions.SupportEmail,
            appName = _appOptions.Name
        };

        return Success(config);
    }
}
