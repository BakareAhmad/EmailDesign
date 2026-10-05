namespace DeChimex.Domain.Common;

/// <summary>Marks an entity that is logically (not physically) removed.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTime? DeletedDateUtc { get; }
    string? DeletedBy { get; }
    void MarkDeleted(string? deletedBy);
}
