using System.Linq.Expressions;
using DeChimex.Domain.Common;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Repositories;

/// <summary>Base implementation of the aggregate repository over EF Core.</summary>
public abstract class RepositoryBase<TAggregate>(DeChimexDbContext context) : IRepository<TAggregate>
    where TAggregate : AggregateRoot
{
    protected readonly DeChimexDbContext Context = context;
    protected DbSet<TAggregate> Set => Context.Set<TAggregate>();

    public virtual async Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await Set.FirstOrDefaultAsync(e => e.Id == id, ct);

    public virtual Task<bool> ExistsAsync(Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default)
        => Set.AnyAsync(predicate, ct);

    public virtual async Task<IReadOnlyList<TAggregate>> ListAsync(CancellationToken ct = default)
        => await Set.AsNoTracking().ToListAsync(ct);

    public virtual async Task AddAsync(TAggregate aggregate, CancellationToken ct = default)
        => await Set.AddAsync(aggregate, ct);

    /// <summary>
    /// Marks an aggregate for update. If the aggregate is already tracked by the
    /// current DbContext (the normal case: it was loaded within this request), we do
    /// NOT call <see cref="DbSet{TEntity}.Update"/> — the change tracker already detects
    /// property mutations automatically. Calling Set.Update would force the ENTIRE object
    /// graph (the aggregate AND every loaded child, e.g. UserRole rows) to
    /// EntityState.Modified, emitting spurious UPDATEs against child tables. When a child
    /// row's key has drifted from the database (e.g. re-seeded link tables), that spurious
    /// UPDATE affects 0 rows and throws DbUpdateConcurrencyException.
    /// For a detached aggregate (loaded AsNoTracking or reconstructed) we attach the root
    /// as Modified without dragging child navigations into a blanket Modified state.
    /// </summary>
    public virtual void Update(TAggregate aggregate)
    {
        var entry = Context.Entry(aggregate);
        if (entry.State == EntityState.Detached)
            entry.State = EntityState.Modified;
        // If already tracked (Unchanged/Modified/Added), do nothing:
        // EF's change tracker will persist only the properties that actually changed.
    }

    public virtual void Remove(TAggregate aggregate) => Set.Remove(aggregate);
}