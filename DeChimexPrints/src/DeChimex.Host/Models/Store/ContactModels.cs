using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Store;

public sealed class ContactFormViewModel
{
    [Required(ErrorMessage = "Please enter your full name.")]
    [StringLength(120, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Please enter a valid email.")]
    [EmailAddress(ErrorMessage = "Please enter a valid email.")]
    [StringLength(200)]
    public string Email { get; set; } = "";

    [StringLength(40, ErrorMessage = "Phone is too long.")]
    public string? Phone { get; set; }

    [StringLength(120)]
    public string? Service { get; set; }

    [Required(ErrorMessage = "Please provide some details.")]
    [StringLength(4000, ErrorMessage = "Message is too long.")]
    public string Details { get; set; } = "";
}
