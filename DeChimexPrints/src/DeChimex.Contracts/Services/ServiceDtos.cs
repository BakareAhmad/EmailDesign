namespace DeChimex.Contracts.Services;

public sealed record ServiceDto(Guid Id, string Name, string Slug, string? Description, decimal BasePrice, string Currency, string? IconColor, bool IsActive, bool IsFeatured);
public sealed record CreateServiceRequest(string Name, string? Description, decimal BasePrice, string? IconColor, bool IsFeatured);
public sealed record UpdateServiceRequest(string Name, string? Description, decimal BasePrice, string? IconColor, bool IsFeatured, bool? IsActive = null);
