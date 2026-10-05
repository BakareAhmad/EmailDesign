using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
namespace DeChimex.Persistence.Repositories;
public sealed class MajorCategoryRepository(DeChimexDbContext db) : RepositoryBase<MajorCategory>(db), IMajorCategoryRepository
{
    public async Task<int> GetMaxDisplayOrderAsync(CancellationToken ct = default)
        => await Set.MaxAsync(x => (int?)x.DisplayOrder, ct) ?? 0;
}
