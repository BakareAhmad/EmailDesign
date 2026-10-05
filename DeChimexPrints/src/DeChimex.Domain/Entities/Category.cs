using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>Product category (e.g. Jersey, Mug, Banner). Categories represent the actual
/// product TYPE and carry the ordering requirements (CategoryOptions) that every product
/// in the category shares. There is intentionally no product-level option system.</summary>
public sealed class Category : AggregateRoot
{
    private readonly List<CategoryOption> _options = new();

    private Category() { } // EF

    private Category(string name, string? description, int displayOrder)
    {
        Name = name;
        Slug = Slug.FromText(name);
        Description = description;
        DisplayOrder = displayOrder;
        IsActive = true;
    }

    public Guid? MajorCategoryId { get; private set; }
    public MajorCategory? MajorCategory { get; private set; }
    public void AssignMajorCategory(Guid? id) => MajorCategoryId = id;

    public string Name { get; private set; } = default!;
    public Slug Slug { get; private set; } = default!;
    public string? Description { get; private set; }
    public int DisplayOrder { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>The ordering questions a customer answers when ordering any product in
    /// this category. Reusable across products; snapshotted onto orders on submission.</summary>
    public IReadOnlyCollection<CategoryOption> Options => _options.AsReadOnly();

    public static Category Create(string name, string? description = null, int displayOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("category.name", "Category name is required.");
        return new Category(name.Trim(), description?.Trim(), displayOrder);
    }

    public void Update(string name, string? description, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("category.name", "Category name is required.");
        Name = name.Trim();
        Slug = Slug.FromText(name);
        Description = description?.Trim();
        DisplayOrder = displayOrder;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Replaces the category's entire option set in one operation (delete + re-add).
    /// Historical orders are unaffected because responses snapshot question/type/value.
    /// Rules enforced here:
    ///  - single/multiple choice options require at least one choice;
    ///  - a conditional parent must reference a DIFFERENT choice-type option EARLIER in
    ///    display order (guarantees no cycles);
    ///  - text/long-text/file options ignore any supplied choices.
    /// </summary>
    public void ReplaceOptions(IReadOnlyList<CategoryOptionSpec> specs)
    {
        var byKey = new Dictionary<string, CategoryOption>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: validate keys + types before mutating anything.
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            if (string.IsNullOrWhiteSpace(s.Key) || !seen.Add(s.Key))
                throw new BusinessRuleValidationException("categoryoption.key", "Each option needs a unique key.");
            if (string.IsNullOrWhiteSpace(s.Question))
                throw new BusinessRuleValidationException("categoryoption.question", "The question text is required.");
            if (s.Type is CategoryOptionType.SingleChoice or CategoryOptionType.MultipleChoice
                && s.Choices.Count == 0)
                throw new BusinessRuleValidationException("categoryoption.choices",
                    $"\"{s.Question}\" needs at least one choice.");
        }

        // Remove existing options so the whole set is rebuilt (children cascade-delete).
        foreach (var existing in _options.ToList())
            _options.Remove(existing);

        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            Guid? parentId = null;
            if (!string.IsNullOrWhiteSpace(s.ParentKey))
            {
                if (!byKey.TryGetValue(s.ParentKey, out var parent))
                    throw new BusinessRuleValidationException("categoryoption.parent",
                        $"\"{s.Question}\" references an unknown option.");
                if (parent.Type is not (CategoryOptionType.SingleChoice or CategoryOptionType.MultipleChoice))
                    throw new BusinessRuleValidationException("categoryoption.parent",
                        $"\"{s.Question}\" can only depend on a choice question.");
                parentId = parent.Id;
            }

            var option = CategoryOption.Create(Id, s.Question, s.Type, s.IsRequired,
                i, parentId, s.ShowWhenValue);

            // Non-choice types have no choices; the value they collect is free-form.
            if (s.Type is CategoryOptionType.SingleChoice or CategoryOptionType.MultipleChoice)
                foreach (var label in s.Choices)
                    option.AddChoice(label);

            _options.Add(option);
            byKey[s.Key] = option;
        }
    }
}
