using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.Features.Categories.Commands;

public sealed record DeleteCategoryCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteCategoryHandler(ICategoryRepository categories, IProductRepository products, IUnitOfWork uow, DeChimex.Application.Common.Interfaces.ICacheService cache)
    : ICommandHandler<DeleteCategoryCommand, Unit>
{
    public async Task<Unit> Handle(DeleteCategoryCommand command, CancellationToken ct)
    {
        var category = await categories.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Category", command.Id);

        var inUse = await products.ExistsAsync(p => p.CategoryId == category.Id && !p.IsDeleted, ct);
        if (inUse)
            throw new BusinessRuleValidationException("category.in_use", "This category still has products assigned to it. Reassign or delete those products first.");

        categories.Remove(category); // soft delete via interceptor
        await uow.SaveChangesAsync(ct);
        foreach (var key in DeChimex.Application.Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct); // MED-004
        return Unit.Value;
    }
}
