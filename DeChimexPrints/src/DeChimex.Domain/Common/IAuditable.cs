namespace DeChimex.Domain.Common;

/// <summary>Marks an entity whose audit fields are automatically populated on save.</summary>
public interface IAuditable
{
    DateTime CreatedDateUtc { get; set; }
    string? CreatedBy { get; set; }
    DateTime? ModifiedDateUtc { get; set; }
    string? ModifiedBy { get; set; }
}
