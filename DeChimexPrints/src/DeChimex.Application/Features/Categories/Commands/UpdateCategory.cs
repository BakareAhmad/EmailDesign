using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Categories;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Categories.Commands;

public sealed record UpdateCategoryCommand(Guid Id, UpdateCategoryRequest Request) : ICommand<CategoryDto>;

public sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Request.Description).MaximumLength(1000);
    }
}

public sealed class UpdateCategoryHandler(ICategoryRepository categories, IUnitOfWork uow, DeChimex.Application.Common.Interfaces.ICacheService cache, IMajorCategoryRepository majors)
    : ICommandHandler<UpdateCategoryCommand, CategoryDto>
{
    public async Task<CategoryDto> Handle(UpdateCategoryCommand command, CancellationToken ct)
    {
        var category = await categories.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Category", command.Id);

        if (await categories.NameExistsAsync(command.Request.Name, command.Id, ct))
            throw new ConflictException("category.exists", $"A category named '{command.Request.Name}' already exists.");

        if (command.Request.UpdateMajorCategory)
        {
            if (command.Request.MajorCategoryId is Guid majorId && await majors.GetByIdAsync(majorId, ct) is null)
                throw new ConflictException("category.major_missing", "Select an existing major category or leave it unassigned.");
            category.AssignMajorCategory(command.Request.MajorCategoryId);
        }
        category.Update(command.Request.Name, command.Request.Description,
            command.Request.DisplayOrder ?? category.DisplayOrder);
        if (command.Request.IsActive is bool active)
        {
            if (active) category.Activate(); else category.Deactivate();
        }
        categories.Update(category);
        await uow.SaveChangesAsync(ct);
        foreach (var key in DeChimex.Application.Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct); // MED-004

        return new CategoryDto(category.Id, category.Name, category.Slug.Value, category.Description,
            category.DisplayOrder, category.IsActive, 0, category.MajorCategoryId);
    }
}
