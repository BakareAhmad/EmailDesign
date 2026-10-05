namespace DeChimex.Contracts.Customers;

public sealed record AddressDto(Guid Id, string Label, string Street, string City, string State, string Country, string? PostalCode, bool IsDefault);
public sealed record CustomerDto(Guid Id, string FirstName, string LastName, string FullName, string? Email, bool IsGuest, string? Phone, string? Notes, decimal LifetimeSpending, DateTime CreatedDateUtc, IReadOnlyList<AddressDto> Addresses);
public sealed record UpdateCustomerProfileRequest(string FirstName, string LastName, string? Phone, string? Notes);
public sealed record AddAddressRequest(string Label, string Street, string City, string State, string Country, string? PostalCode, bool IsDefault);

/// <summary>Admin/sales creating a customer record (e.g. a walk-in) not tied to a login account.</summary>
public sealed record CreateCustomerRequest(string FirstName, string LastName, string? Email, string? Phone, string? Notes);
