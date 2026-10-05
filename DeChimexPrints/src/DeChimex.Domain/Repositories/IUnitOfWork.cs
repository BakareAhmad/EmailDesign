namespace DeChimex.Domain.Repositories;

/// <summary>
/// Coordinates saving changes across repositories within a single transaction and
/// triggers dispatch of domain events collected on aggregates.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
