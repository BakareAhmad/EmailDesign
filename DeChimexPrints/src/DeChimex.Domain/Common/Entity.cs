namespace DeChimex.Domain.Common;

/// <summary>
/// Base class for all persisted entities. Uses a Guid identity to avoid exposing
/// sequential internal keys and to support distributed generation.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    public override bool Equals(object? obj)
    {
        if (obj is not Entity other) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return Id != Guid.Empty && Id == other.Id;
    }

    public override int GetHashCode() => Id.GetHashCode();

    public static bool operator ==(Entity? a, Entity? b) => Equals(a, b);
    public static bool operator !=(Entity? a, Entity? b) => !Equals(a, b);
}
