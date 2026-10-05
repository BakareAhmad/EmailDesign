using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using MediatR;

namespace DeChimex.Application.Features.Categories.Commands;

/// <summary>Replaces a category's ordering-option set (staff). Historical orders are
/// unaffected: responses snapshot question/type/value at submission time.</summary>
public sealed record SaveCategoryOptionsCommand(Guid CategoryId, SaveCategoryOptionsRequest Request) : ICommand<Unit>;

public sealed class SaveCategoryOptionsHandler(ICategoryRepository categories, IUnitOfWork uow)
    : ICommandHandler<SaveCategoryOptionsCommand, Unit>
{
    public async Task<Unit> Handle(SaveCategoryOptionsCommand command, CancellationToken ct)
    {
        var category = await categories.GetWithOptionsAsync(command.CategoryId, ct)
            ?? throw new NotFoundException("Category", command.CategoryId);

        var specs = command.Request.Options.Select((o, i) => new CategoryOptionSpec(
            string.IsNullOrWhiteSpace(o.Key) ? $"opt{i + 1}" : o.Key.Trim(),
            o.Question,
            ParseType(o.Type),
            o.IsRequired,
            string.IsNullOrWhiteSpace(o.ParentKey) ? null : o.ParentKey.Trim(),
            o.ShowWhenValue,
            o.Choices ?? Array.Empty<string>())).ToList();

        category.ReplaceOptions(specs);
        categories.Update(category);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }

    private static CategoryOptionType ParseType(string type)
    {
        if (!Enum.TryParse<CategoryOptionType>(type, true, out var parsed))
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["Type"] = new[] { "Unknown option type." }
            });
        return parsed;
    }
}
