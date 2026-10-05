using DeChimex.Application.Features.Orders;
using DeChimex.Contracts.Orders;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Orders;

public class SalesAssignmentPolicyTests
{
    private static SalesStaffOptionDto Staff(string email, int workload, DateTime created)
        => new(Guid.NewGuid(), "Sales " + email, email, workload, created);

    [Fact]
    public void Pick_NoEligibleStaff_ReturnsNull()
    {
        SalesAssignmentPolicy.Pick(new List<SalesStaffOptionDto>()).Should().BeNull();
    }

    [Fact]
    public void Pick_SingleCandidate_ReturnsIt()
    {
        var only = Staff("solo@sales.test", 0, DateTime.UtcNow);

        SalesAssignmentPolicy.Pick(new[] { only }).Should().Be(only);
    }

    [Fact]
    public void Pick_PrefersLowestWorkload()
    {
        var busy = Staff("busy@sales.test", 5, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var idle = Staff("idle@sales.test", 1, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        SalesAssignmentPolicy.Pick(new[] { busy, idle })!.Email.Should().Be("idle@sales.test");
    }

    [Fact]
    public void Pick_EqualWorkload_BreaksTieByEarliestAccount()
    {
        var newer = Staff("newer@sales.test", 2, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var older = Staff("older@sales.test", 2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        SalesAssignmentPolicy.Pick(new[] { newer, older })!.Email.Should().Be("older@sales.test");
    }

    [Fact]
    public void Pick_EqualWorkloadAndCreated_BreaksTieByEmail()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var b = Staff("b@sales.test", 0, created);
        var a = Staff("a@sales.test", 0, created);

        SalesAssignmentPolicy.Pick(new[] { b, a })!.Email.Should().Be("a@sales.test");
    }
}
