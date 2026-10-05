using DeChimex.Domain.Entities;
namespace DeChimex.Domain.Repositories;
public interface IMajorCategoryRepository : IRepository<MajorCategory>
{
    Task<int> GetMaxDisplayOrderAsync(CancellationToken ct = default);
}
