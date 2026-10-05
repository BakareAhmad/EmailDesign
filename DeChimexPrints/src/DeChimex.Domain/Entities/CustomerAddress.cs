using DeChimex.Domain.Common;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>A saved address for a customer. Child of the Customer aggregate.</summary>
public sealed class CustomerAddress : Entity
{
    private CustomerAddress() { } // EF

    private CustomerAddress(Guid customerId, string label, Address address, bool isDefault)
    {
        CustomerId = customerId;
        Label = label;
        Address = address;
        IsDefault = isDefault;
    }

    public Guid CustomerId { get; private set; }
    public string Label { get; private set; } = default!;
    public Address Address { get; private set; } = default!;
    public bool IsDefault { get; private set; }

    internal static CustomerAddress Create(Guid customerId, string label, Address address, bool isDefault)
        => new(customerId, label, address, isDefault);

    internal void SetDefault(bool isDefault) => IsDefault = isDefault;
    internal void UpdateAddress(string label, Address address)
    {
        Label = label;
        Address = address;
    }
}
