using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Models.Store;

/// <summary>Bind target for the customer order-request form. The dynamic option answers are
/// read from the raw form collection (keys "opt_{optionId}") because the option set is
/// configurable per category; this model only carries the fixed fields.</summary>
public sealed class OrderRequestFormViewModel
{
    public Guid ProductId { get; set; }

    [BindProperty(Name = "quantity")]
    public int Quantity { get; set; } = 1;

    [BindProperty(Name = "notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Round-tripped form state so a validation error returns the customer to the order form
/// with their valid answers still filled in (files cannot be preserved by the browser and
/// must be re-attached, which the error message explains).
/// </summary>
public sealed class OrderFormDraft
{
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }

    /// <summary>optionId -> submitted value(s). Choice values are stored as the raw selected labels.</summary>
    public Dictionary<string, string[]> Responses { get; set; } = new();
}
