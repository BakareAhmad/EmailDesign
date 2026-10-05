using DeChimex.Application.Features.Orders;
using DeChimex.Contracts.Orders;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Orders;

public class CustomOrderValidationTests
{
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();

    private static IReadOnlyList<CategoryOptionDto> Options() => new[]
    {
        new CategoryOptionDto(Parent, "Need delivery?", "select", false, 1, null, null, Array.Empty<CategoryOptionChoiceDto>()),
        new CategoryOptionDto(Child, "Delivery address", "text", true, 2, Parent, "Yes", Array.Empty<CategoryOptionChoiceDto>())
    };

    [Fact]
    public void Null_parent_answer_does_not_crash_and_hides_the_conditional_question()
    {
        // A null answer (possible from a JSON client) used to throw NullReferenceException.
        var responses = new[] { new OrderRequirementInput(Parent, "Need delivery?", "select", null!) };
        CustomOrderValidation.MissingRequiredQuestions(Options(), responses).Should().BeEmpty();
    }

    [Fact]
    public void Matching_parent_answer_requires_the_conditional_question()
    {
        var responses = new[] { new OrderRequirementInput(Parent, "Need delivery?", "select", "Yes") };
        CustomOrderValidation.MissingRequiredQuestions(Options(), responses).Should().ContainSingle().Which.Should().Be("Delivery address");
    }

    [Fact]
    public void Json_array_parent_answer_is_matched()
    {
        var responses = new[] { new OrderRequirementInput(Parent, "Need delivery?", "checkbox", "[\"No\",\"yes\"]") };
        CustomOrderValidation.MissingRequiredQuestions(Options(), responses).Should().ContainSingle();
    }
}
