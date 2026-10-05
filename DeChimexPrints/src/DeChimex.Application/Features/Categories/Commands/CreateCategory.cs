using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Categories;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentValidation;

namespace DeChimex.Application.Features.Categories.Commands;

public sealed record CreateCategoryCommand(CreateCategoryRequest Request) : ICommand<CategoryDto>;

public sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Request.Description).MaximumLength(1000);
        // An explicit order is optional; when omitted the handler appends the category at the end.
        When(x => x.Request.DisplayOrder.HasValue, () =>
            RuleFor(x => x.Request.DisplayOrder!.Value).GreaterThanOrEqualTo(0));
    }
}

public sealed class CreateCategoryHandler(ICategoryRepository categories, IUnitOfWork uow, DeChimex.Application.Common.Interfaces.ICacheService cache, IMajorCategoryRepository majors)
    : ICommandHandler<CreateCategoryCommand, CategoryDto>
{
    public async Task<CategoryDto> Handle(CreateCategoryCommand command, CancellationToken ct)
    {
        var r = command.Request;
        if (await categories.NameExistsAsync(r.Name, null, ct))
            throw new ConflictException("category.exists", $"A category named '{r.Name}' already exists.");

        // Owner rule (2026-09-09): display order is automatic - a new category always goes
        // to the last spot unless an explicit order is supplied (seeding/migration callers).
        var displayOrder = r.DisplayOrder ?? await categories.GetMaxDisplayOrderAsync(ct) + 1;

        if (r.MajorCategoryId is Guid majorId && await majors.GetByIdAsync(majorId, ct) is null)
            throw new ConflictException("category.major_missing", "Select an existing major category or leave it unassigned.");
        var category = Category.Create(r.Name, r.Description, displayOrder);
        category.AssignMajorCategory(r.MajorCategoryId);
        await categories.AddAsync(category, ct);
        await uow.SaveChangesAsync(ct);
        foreach (var key in DeChimex.Application.Common.CacheKeys.CategoryLists) await cache.RemoveAsync(key, ct); // MED-004

        return new CategoryDto(category.Id, category.Name, category.Slug.Value, category.Description,
            category.DisplayOrder, category.IsActive, 0, category.MajorCategoryId);
    }
}
