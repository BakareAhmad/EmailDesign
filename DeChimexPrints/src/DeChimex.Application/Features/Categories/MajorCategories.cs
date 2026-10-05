using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Categories;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;
namespace DeChimex.Application.Features.Categories;

public interface IMajorCategoryQueries
{
    Task<IReadOnlyList<MajorCategoryDto>> GetAllAsync(CancellationToken ct);
}
public sealed record GetMajorCategoriesQuery : IQuery<IReadOnlyList<MajorCategoryDto>>;
public sealed class GetMajorCategoriesHandler(IMajorCategoryQueries queries) : IQueryHandler<GetMajorCategoriesQuery, IReadOnlyList<MajorCategoryDto>>
{
    public Task<IReadOnlyList<MajorCategoryDto>> Handle(GetMajorCategoriesQuery query, CancellationToken ct) => queries.GetAllAsync(ct);
}
public sealed record SaveMajorCategoryCommand(Guid? Id, string Name, string? Description) : ICommand<Guid>;
public sealed class SaveMajorCategoryValidator : AbstractValidator<SaveMajorCategoryCommand>
{
    public SaveMajorCategoryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}
public sealed class SaveMajorCategoryHandler(IMajorCategoryRepository majors, IUnitOfWork uow, ICacheService cache) : ICommandHandler<SaveMajorCategoryCommand, Guid>
{
    public async Task<Guid> Handle(SaveMajorCategoryCommand command, CancellationToken ct)
    {
        var normalized = MajorCategory.Normalize(command.Name);
        if (await majors.ExistsAsync(m => m.NormalizedName == normalized && (!command.Id.HasValue || m.Id != command.Id), ct))
            throw new ConflictException("majorcategory.exists", "A major category with this name already exists.");
        MajorCategory major;
        if (command.Id is Guid id)
        {
            major = await majors.GetByIdAsync(id, ct) ?? throw new NotFoundException("Major category", id);
            major.Update(command.Name, command.Description); majors.Update(major);
        }
        else
        {
            major = MajorCategory.Create(command.Name, command.Description, await majors.GetMaxDisplayOrderAsync(ct) + 1);
            await majors.AddAsync(major, ct);
        }
        await uow.SaveChangesAsync(ct);
        foreach (var key in Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct);
        return major.Id;
    }
}
public sealed record DeleteMajorCategoryCommand(Guid Id) : ICommand<Unit>;
public sealed class DeleteMajorCategoryHandler(IMajorCategoryRepository majors, ICategoryRepository categories, IUnitOfWork uow, ICacheService cache) : ICommandHandler<DeleteMajorCategoryCommand, Unit>
{
    public async Task<Unit> Handle(DeleteMajorCategoryCommand command, CancellationToken ct)
    {
        var major = await majors.GetByIdAsync(command.Id, ct) ?? throw new NotFoundException("Major category", command.Id);
        if (await categories.ExistsAsync(c => c.MajorCategoryId == command.Id, ct))
            throw new ConflictException("majorcategory.in_use", "Move or unassign all subcategories before deleting this major category.");
        majors.Remove(major); await uow.SaveChangesAsync(ct);
        foreach (var key in Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct);
        return Unit.Value;
    }
}
