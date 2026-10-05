using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Admin;

public sealed class CustomerFormViewModel
{
    public Guid? CustomerId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Line in the quote invoice form: exactly one of ProductId/ServiceId.</summary>
public sealed class QuoteLineViewModel
{
    public string? ProductId { get; set; }
    public string? ServiceId { get; set; }
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
}

/// <summary>Staff quote invoice form: pick customer, add product/service lines, discount/tax, notes.</summary>
public sealed class QuoteInvoiceFormViewModel
{
    /// <summary>"existing" or "new".</summary>
    public string CustomerMode { get; set; } = "existing";
    public Guid? CustomerId { get; set; }
    public string? NewFirstName { get; set; }
    public string? NewLastName { get; set; }
    public string? NewEmail { get; set; }
    public string? NewPhone { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public string? Notes { get; set; }
    public List<QuoteLineViewModel> Lines { get; set; } = new();
}

public sealed class OrderBuilderViewModel
{
    /// <summary>Quote invoice this order is placed from (null for regular POS orders).</summary>
    public Guid? InvoiceId { get; set; }

    /// <summary>"existing" or "new".</summary>
    public string CustomerMode { get; set; } = "existing";

    public Guid? CustomerId { get; set; }

    public string? NewFirstName { get; set; }
    public string? NewLastName { get; set; }
    public string? NewEmail { get; set; }
    public string? NewPhone { get; set; }

    /// <summary>"delivery" or "pickup".</summary>
    public string Fulfilment { get; set; } = "delivery";

    /// <summary>
    /// Optional payment taken at the counter while creating the order:
    /// "Cash", "BankTransfer" or "Paystack" (reference). Null/blank leaves
    /// the order unpaid (Draft) for later "Mark as paid".
    /// </summary>
    public string? PaymentMethod { get; set; }

    [StringLength(500, ErrorMessage = "Payment note is too long.")]
    public string? PaymentNote { get; set; }

    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Discount cannot be negative.")]
    public decimal Discount { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Tax cannot be negative.")]
    public decimal Tax { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public List<OrderLineForm> Products { get; set; } = new() { new() };
    public List<OrderServiceForm> Services { get; set; } = new() { new() };
}

public sealed class OrderLineForm
{
    public Guid? ProductId { get; set; }
    public int Quantity { get; set; } = 1;
}

public sealed class OrderServiceForm
{
    public Guid? ServiceId { get; set; }
    public int Quantity { get; set; } = 1;
    [StringLength(500)]
    public string? Notes { get; set; }
}

public sealed class MarkPaidFormViewModel
{
    public const string Cash = "Cash";
    public const string BankTransfer = "BankTransfer";
    public const string Paystack = "Paystack";

    [Required]
    public string Method { get; set; } = Cash;
    public string? Note { get; set; }
}

public sealed class OrderCancelFormViewModel
{
    [StringLength(500, ErrorMessage = "Reason is too long.")]
    public string? Reason { get; set; }

    /// <summary>AUD-018: what happens to a HELD payment when the order is cancelled.
    /// RefundLater (default) | StoreCredit | NotRequired.</summary>
    public string? RefundOutcome { get; set; }
}

public sealed class AdminCustomerAddressFormViewModel
{
    public string Label { get; set; } = "";
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>Form models for admin MVC pages. Separate from the Contracts DTOs because
/// model binding needs settable properties (Contracts use positional records).</summary>

public sealed class ProductFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(200, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = string.Empty;

    // SKU is auto-suggested from the product name and may be left blank (the create
    // handler generates one). When supplied it must match the exact 9-char format,
    // which is enforced server-side by the create validator.
    [StringLength(50, ErrorMessage = "SKU is too long.")]
    public string Sku { get; set; } = string.Empty;

    [StringLength(4000, ErrorMessage = "Description is too long.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Category is required.")]
    public Guid CategoryId { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Selling price must be greater than zero.")]
    public decimal SellingPrice { get; set; }

    public bool IsCustomizable { get; set; }
    public bool IsFeatured { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "Quantity cannot be negative.")]
    public int InitialQuantity { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "Threshold cannot be negative.")]
    public int LowStockThreshold { get; set; }

    /// <summary>New images to upload (JPEG, PNG, WebP, GIF). First uploaded image becomes primary.
    /// At least one image is required when creating a product (enforced in the controller,
    /// since IFormFile arrays cannot carry DataAnnotations) and when editing a product that
    /// currently has no images.</summary>
    public Microsoft.AspNetCore.Http.IFormFile[]? Images { get; set; }
}

public sealed class CategoryFormViewModel
{
    public Guid? MajorCategoryId { get; set; }
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(120, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "Description is too long.")]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class ServiceFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(200, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(4000, ErrorMessage = "Description is too long.")]
    public string? Description { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Price cannot be negative.")]
    public decimal BasePrice { get; set; }

    [StringLength(20, ErrorMessage = "Color is too long.")]
    public string? IconColor { get; set; }

    public bool IsFeatured { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class ChangePasswordFormViewModel
{
    [Required(ErrorMessage = "Current password is required.")]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "New password is required.")]
    [DataType(DataType.Password)]
    [MinLength(8, ErrorMessage = "New password must be at least 8 characters.")]
    public string NewPassword { get; set; } = "";

    [Required(ErrorMessage = "Confirm the new password.")]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}
