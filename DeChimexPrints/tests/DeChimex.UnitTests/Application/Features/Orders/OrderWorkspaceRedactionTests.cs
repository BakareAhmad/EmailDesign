using DeChimex.Contracts.Orders;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Orders;

public class OrderWorkspaceRedactionTests
{
    private static OrderWorkspaceDto WorkspaceWith(params OrderTimelineEntryDto[] timeline)
        => new(
            Guid.NewGuid(), "DCX-1", Guid.NewGuid(), "Test Customer", "Custom", "AwaitingReview",
            "Order Received", "next", false,
            0, 0, 0, 0, 0, "NGN", DateTime.UtcNow,
            [], [], [], [], [], [], timeline.ToList(), [], [], null,
            "Sales Alpha", Guid.NewGuid(), "Active");

    [Fact]
    public void RedactForCustomer_DropsAssigneeName_KeepsMarker()
    {
        var redacted = WorkspaceWith().RedactForCustomer();

        redacted.AssignedSalesName.Should().BeNull();
        redacted.HasAssignedSales.Should().BeTrue();
    }

    [Fact]
    public void RedactForCustomer_RewritesAssignmentTimeline_Generic()
    {
        var redacted = WorkspaceWith(
            new OrderTimelineEntryDto(Guid.NewGuid(), "assigned",
                "Automatically assigned to sales (Sales Alpha).", "System", DateTime.UtcNow),
            new OrderTimelineEntryDto(Guid.NewGuid(), "reassigned",
                "Reassigned from Sales Alpha to Sales Beta by owner@x.", "owner@x", DateTime.UtcNow),
            new OrderTimelineEntryDto(Guid.NewGuid(), "message",
                "customer sent a message.", "Test Customer", DateTime.UtcNow)).RedactForCustomer();

        redacted.Timeline.Should().HaveCount(3);
        redacted.Timeline[0].Description.Should().Be("A sales specialist was assigned to your request.");
        redacted.Timeline[0].ActorName.Should().BeNull();
        redacted.Timeline[1].Description.Should().Be("Your request was handed over to another sales specialist.");
        redacted.Timeline[1].ActorName.Should().BeNull();
        redacted.Timeline[2].Description.Should().Be("customer sent a message.");
        var joined = string.Join(" ", redacted.Timeline.Select(t => t.Description));
        joined.Should().NotContain("Sales Alpha").And.NotContain("Sales Beta");
    }

    [Fact]
    public void RedactForCustomer_Unassigned_StaysUnmarked()
    {
        var ws = WorkspaceWith(
            new OrderTimelineEntryDto(Guid.NewGuid(), "unassigned",
                "No eligible sales staff — awaiting manual assignment.", "System", DateTime.UtcNow))
            with { AssignedSalesName = null, AssignedSalesUserId = null };

        var redacted = ws.RedactForCustomer();

        redacted.HasAssignedSales.Should().BeFalse();
        redacted.Timeline[0].Description.Should().Be("Our team will assign a sales specialist shortly.");
    }
}
