using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models;

/// <summary>Model for the MVC customer registration / sign up form.</summary>
public sealed class RegisterViewModel
{
    /// <summary>AUD-024 honeypot: hidden in the UI (CSS), never filled by humans. A value
    /// means a bot submitted the form — the controller pretends success and does nothing.</summary>
    public string? CompanyWebsite { get; set; }

    [Required(ErrorMessage = "First name is required.")]
    [MaxLength(80)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required.")]
    [MaxLength(80)]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Phone number is required.")]
    [MaxLength(20)]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please confirm your password.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}
