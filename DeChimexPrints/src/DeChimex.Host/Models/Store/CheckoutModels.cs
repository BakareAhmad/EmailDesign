using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Store;

public sealed class CheckoutFormViewModel
{
    /// <summary>"delivery" or "pickup".</summary>
    [Required]
    public string Fulfilment { get; set; } = "delivery";

    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }
    public string? Notes { get; set; }
}
