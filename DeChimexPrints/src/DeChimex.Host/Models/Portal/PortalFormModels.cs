using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Portal;

/// <summary>Form models for the customer portal MVC pages.</summary>

public sealed class ProfileFormViewModel
{
    [Required(ErrorMessage = "First name is required.")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required.")]
    public string LastName { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? Notes { get; set; }
}

public sealed class AddressFormViewModel
{
    [Required(ErrorMessage = "Label is required.")]
    public string Label { get; set; } = string.Empty;

    [Required(ErrorMessage = "Street is required.")]
    public string Street { get; set; } = string.Empty;

    [Required(ErrorMessage = "City is required.")]
    public string City { get; set; } = string.Empty;

    [Required(ErrorMessage = "State is required.")]
    public string State { get; set; } = string.Empty;

    [Required(ErrorMessage = "Country is required.")]
    public string Country { get; set; } = "Nigeria";

    public string? PostalCode { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class ChangePasswordFormViewModel
{
    [Required(ErrorMessage = "Current password is required.")]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "New password is required.")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    [RegularExpression("^(?=.*[A-Z])(?=.*[a-z])(?=.*[0-9]).*$",
        ErrorMessage = "Password must contain upper and lower case letters and a number.")]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
