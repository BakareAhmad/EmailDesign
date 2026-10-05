using DeChimex.Application.Features.Categories;
using DeChimex.Contracts.Categories;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
namespace DeChimex.Persistence.Queries;
public sealed class MajorCategoryQueries(DeChimexDbContext db) : IMajorCategoryQueries
{
    public async Task<IReadOnlyList<MajorCategoryDto>> GetAllAsync(CancellationToken ct)
        => await db.MajorCategories.AsNoTracking().OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name)
            .Select(m => new MajorCategoryDto(m.Id, m.Name, m.Description, m.DisplayOrder,
                db.Categories.Count(c => c.MajorCategoryId == m.Id))).ToListAsync(ct);
}
