using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A staff-configured ordering question on a Category (reusable by every product in
/// that category — there is deliberately no product-level option system). Answers are
/// snapshotted onto the order when a customer submits an order request, so editing or
/// deleting an option later never corrupts historical orders.
/// </summary>
public sealed class CategoryOption : Entity
{
    private readonly List<CategoryOptionChoice> _choices = new();

    private CategoryOption() { } // EF

    private CategoryOption(Guid categoryId, string question, CategoryOptionType type,
        bool isRequired, int displayOrder, Guid? parentOptionId, string? showWhenValue)
    {
        CategoryId = categoryId;
        Question = question;
        Type = type;
        IsRequired = isRequired;
        DisplayOrder = displayOrder;
        ParentOptionId = parentOptionId;
        ShowWhenValue = showWhenValue;
        IsActive = true;
    }

    public Guid CategoryId { get; private set; }
    public string Question { get; private set; } = default!;
    public CategoryOptionType Type { get; private set; }
    public bool IsRequired { get; private set; }
    public int DisplayOrder { get; private set; }

    /// <summary>Conditional visibility: null = always shown. Otherwise this option is
    /// revealed only when the parent option's answer equals (choice/text) or contains
    /// (multiple choice) this value.</summary>
    public Guid? ParentOptionId { get; private set; }
    public string? ShowWhenValue { get; private set; }
    public bool IsActive { get; private set; }

    public IReadOnlyCollection<CategoryOptionChoice> Choices => _choices.AsReadOnly();

    public static CategoryOption Create(Guid categoryId, string question, CategoryOptionType type,
        bool isRequired, int displayOrder, Guid? parentOptionId = null, string? showWhenValue = null)
    {
        if (categoryId == Guid.Empty)
            throw new BusinessRuleValidationException("categoryoption.category", "A category is required.");
        if (string.IsNullOrWhiteSpace(question))
            throw new BusinessRuleValidationException("categoryoption.question", "The question text is required.");
        if (!Enum.IsDefined(type))
            throw new BusinessRuleValidationException("categoryoption.type", "Unknown option type.");

        return new CategoryOption(categoryId, question.Trim(), type, isRequired, displayOrder,
            parentOptionId, string.IsNullOrWhiteSpace(showWhenValue) ? null : showWhenValue.Trim());
    }

    public CategoryOptionChoice AddChoice(string label)
    {
        if (Type is not (CategoryOptionType.SingleChoice or CategoryOptionType.MultipleChoice))
            throw new BusinessRuleValidationException("categoryoption.choices",
                "Only single/multiple choice options can have choices.");
        if (string.IsNullOrWhiteSpace(label))
            throw new BusinessRuleValidationException("categoryoption.choice", "Choice label is required.");
        var choice = CategoryOptionChoice.Create(Id, label, _choices.Count);
        _choices.Add(choice);
        return choice;
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;

    /// <summary>Whether the given value would reveal a conditional child option.</summary>
    public bool Reveals(string? childShowWhenValue, IReadOnlyCollection<string> parentValues)
    {
        if (string.IsNullOrWhiteSpace(childShowWhenValue)) return true;
        return Type == CategoryOptionType.MultipleChoice
            ? parentValues.Contains(childShowWhenValue, StringComparer.OrdinalIgnoreCase)
            : parentValues.Any(v => string.Equals(v, childShowWhenValue, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One selectable choice on a single/multiple-choice category option.</summary>
public sealed class CategoryOptionChoice : Entity
{
    private CategoryOptionChoice() { } // EF
    private CategoryOptionChoice(Guid categoryOptionId, string label, int displayOrder)
    {
        CategoryOptionId = categoryOptionId;
        Label = label;
        DisplayOrder = displayOrder;
    }

    public Guid CategoryOptionId { get; private set; }
    public string Label { get; private set; } = default!;
    public int DisplayOrder { get; private set; }

    public static CategoryOptionChoice Create(Guid categoryOptionId, string label, int displayOrder)
    {
        if (categoryOptionId == Guid.Empty)
            throw new BusinessRuleValidationException("categoryoption.choice.option", "An option is required.");
        if (string.IsNullOrWhiteSpace(label))
            throw new BusinessRuleValidationException("categoryoption.choice", "Choice label is required.");
        return new CategoryOptionChoice(categoryOptionId, label.Trim(), displayOrder);
    }
}

/// <summary>Admin-supplied spec for (re)building a category's option set in one operation.</summary>
public sealed record CategoryOptionSpec(
    string Key,
    string Question,
    CategoryOptionType Type,
    bool IsRequired,
    string? ParentKey,
    string? ShowWhenValue,
    IReadOnlyList<string> Choices);
