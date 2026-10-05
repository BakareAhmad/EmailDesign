using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A branding/printing service. Independent of inventory. Ordering a service never
/// deducts stock (the customer typically supplies their own item).
/// </summary>
public sealed class Service : AggregateRoot
{
    private Service() { } // EF

    private Service(string name, string? description, Money basePrice, string? iconColor, bool isActive, bool isFeatured)
    {
        Name = name;
        Slug = Slug.FromText(name);
        Description = description;
        BasePrice = basePrice;
        IconColor = iconColor;
        IsActive = isActive;
        IsFeatured = isFeatured;
    }

    public string Name { get; private set; } = default!;
    public Slug Slug { get; private set; } = default!;
    public string? Description { get; private set; }
    public Money BasePrice { get; private set; } = default!;
    public string? IconColor { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsFeatured { get; private set; }

    public static Service Create(string name, string? description, decimal basePrice,
        string? iconColor = null, bool isFeatured = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("service.name", "Service name is required.");
        return new Service(name.Trim(), description?.Trim(), Money.Of(basePrice), iconColor, true, isFeatured);
    }

    public void Update(string name, string? description, decimal basePrice, string? iconColor, bool isFeatured)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("service.name", "Service name is required.");
        Name = name.Trim();
        Slug = Slug.FromText(name);
        Description = description?.Trim();
        BasePrice = Money.Of(basePrice);
        IconColor = iconColor;
        IsFeatured = isFeatured;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
