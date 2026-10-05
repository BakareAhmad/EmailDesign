namespace DeChimex.Contracts.Categories;
public sealed record MajorCategoryDto(Guid Id, string Name, string? Description, int DisplayOrder, int CategoryCount);
