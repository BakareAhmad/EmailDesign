using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>A fine-grained permission (e.g. "products.create"). Assigned to roles.</summary>
public sealed class Permission : Entity
{
    private Permission() { } // EF

    private Permission(string name, string? description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }

    public static Permission Create(string name, string? description = null) => new(name, description);
}
