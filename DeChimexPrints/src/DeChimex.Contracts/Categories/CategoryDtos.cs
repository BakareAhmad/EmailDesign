namespace DeChimex.Contracts.Categories;

public sealed record CategoryDto(Guid Id, string Name, string Slug, string? Description, int DisplayOrder, bool IsActive, int ProductCount, Guid? MajorCategoryId = null, string? MajorCategoryName = null);
/// <summary>DisplayOrder null = append automatically at the end of the list (owner rule: no manual ordering).</summary>
public sealed record CreateCategoryRequest(string Name, string? Description, int? DisplayOrder = null, Guid? MajorCategoryId = null);
/// <summary>DisplayOrder null = keep the category's current position.</summary>
public sealed record UpdateCategoryRequest(string Name, string? Description, int? DisplayOrder = null, bool? IsActive = null, Guid? MajorCategoryId = null, bool UpdateMajorCategory = false);
