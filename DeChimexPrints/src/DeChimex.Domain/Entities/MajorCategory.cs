using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
namespace DeChimex.Domain.Entities;

/// <summary>Discovery group only. Ordering options and products remain on Category.</summary>
public sealed class MajorCategory : AggregateRoot
{
    private MajorCategory() { }
    public string Name { get; private set; } = default!;
    public string NormalizedName { get; private set; } = default!;
    public string? Description { get; private set; }
    public int DisplayOrder { get; private set; }
    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
    public static MajorCategory Create(string name, string? description = null, int displayOrder = 0)
    {
        var result = new MajorCategory { DisplayOrder = displayOrder };
        result.Update(name, description);
        return result;
    }
    public void Update(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new BusinessRuleValidationException("majorcategory.name", "Name is required and must not exceed 120 characters.");
        if (description?.Length > 1000)
            throw new BusinessRuleValidationException("majorcategory.description", "Description must not exceed 1000 characters.");
        Name = name.Trim(); NormalizedName = Normalize(name); Description = description?.Trim();
    }
}
