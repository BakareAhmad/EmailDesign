using System.Linq.Expressions;
using DeChimex.Domain.Common;

namespace DeChimex.Domain.Repositories;

/// <summary>
/// Generic repository for aggregate roots. Deliberately minimal, with feature-specific
/// query needs are met by dedicated repository interfaces, not by leaking IQueryable.
/// </summary>
public interface IRepository<TAggregate> where TAggregate : AggregateRoot
{
    Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Expression<Func<TAggregate, bool>> predicate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TAggregate>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default);
    void Update(TAggregate aggregate);
    void Remove(TAggregate aggregate);
}
