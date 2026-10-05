using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A supplier providing materials or products to the business.
/// </summary>
public sealed class Supplier : AggregateRoot
{
    private Supplier() { } // EF

    private Supplier(string name, string? contact, string? email, string? phone, string? category, string status)
    {
        Name = name;
        Contact = contact;
        Email = email;
        Phone = phone;
        Category = category;
        Status = status;
    }

    public string Name { get; private set; } = default!;
    public string? Contact { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Category { get; private set; }
    public string Status { get; private set; } = "Active";

    public static Supplier Create(string name, string? contact, string? email, string? phone, string? category, string? status)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("supplier.name", "Supplier name is required.");

        return new Supplier(
            name.Trim(),
            contact?.Trim(),
            email?.Trim(),
            phone?.Trim(),
            category?.Trim(),
            string.IsNullOrWhiteSpace(status) ? "Active" : status.Trim());
    }

    public void Update(string name, string? contact, string? email, string? phone, string? category, string? status)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("supplier.name", "Supplier name is required.");

        Name = name.Trim();
        Contact = contact?.Trim();
        Email = email?.Trim();
        Phone = phone?.Trim();
        Category = category?.Trim();
        Status = string.IsNullOrWhiteSpace(status) ? Status : status.Trim();
    }

    public void Activate() => Status = "Active";
    public void Deactivate() => Status = "Inactive";
}
