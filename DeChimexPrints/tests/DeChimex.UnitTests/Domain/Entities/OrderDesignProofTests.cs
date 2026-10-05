using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

/// <summary>
/// Design proofs are the formal approval record: every version keeps what was
/// submitted, every decision keeps who/when/why. These tests pin that record —
/// feedback survives approval, both decisions raise staff-visible events, and
/// decisions lock once the order is paid.
/// </summary>
public class OrderDesignProofTests
{
    private static Order NegotiationOrder()
    {
        var order = Order.CreateDraft("ORD-PROOF-001", Guid.NewGuid());
        order.AddProduct(Guid.NewGuid(), "Banner", "BNR-1", 100m, 1);
        order.SubmitOrderRequest();
        order.StartDiscussion();
        return order;
    }

    private static OrderDesignProof Upload(Order order, int version)
        => order.AddProof($"p{version}.bin", $"proof{version}.png",
            $"/uploads/proof{version}.png", "image/png", null);

    [Fact]
    public void Approve_PreservesEarlierChangeRequestFeedback()
    {
        var order = NegotiationOrder();
        var proof = Upload(order, 1);
        order.RequestProofChanges(proof.Id, "Make the logo bigger");

        order.ApproveProof(proof.Id, Guid.NewGuid());

        proof.Status.Should().Be(DesignProofStatus.Approved);
        proof.ApprovedAtUtc.Should().NotBeNull();
        proof.CustomerFeedback.Should().Be("Make the logo bigger",
            "approval must not destroy the recorded reason for the earlier change request");
    }

    [Fact]
    public void RequestProofChanges_RaisesEventWithVersionAndFeedback()
    {
        var order = NegotiationOrder();
        var proof = Upload(order, 1);
        order.ClearDomainEvents();

        order.RequestProofChanges(proof.Id, "Try a darker blue");

        var evt = order.DomainEvents.OfType<DesignProofChangesRequestedEvent>().Should().ContainSingle().Subject;
        evt.Version.Should().Be(1);
        evt.Feedback.Should().Be("Try a darker blue");
        evt.OrderId.Should().Be(order.Id);
        order.Status.Should().Be(OrderStatus.InDiscussion);
    }

    [Fact]
    public void Approve_RaisesApprovedEvent_EvenWhenOrderNotAwaitingApproval()
    {
        var order = NegotiationOrder();
        var proof = Upload(order, 1);
        order.RequestProofChanges(proof.Id, "First thoughts");
        order.ClearDomainEvents();

        order.ApproveProof(proof.Id, Guid.NewGuid());

        order.DomainEvents.OfType<DesignProofApprovedEvent>().Should().ContainSingle();
        order.Status.Should().Be(OrderStatus.InDiscussion,
            "only the AwaitingDesignApproval -> RequirementsConfirmed hop is conditional, not the decision itself");
    }

    [Fact]
    public void Approve_AfterPayment_ThrowsImmutable()
    {
        var order = NegotiationOrder();
        var proof = Upload(order, 1);
        order.RequestProofChanges(proof.Id, "Adjust the margins");
        order.FinalizeOrder();
        order.MarkPaid();

        var act = () => order.ApproveProof(proof.Id, Guid.NewGuid());

        act.Should().Throw<BusinessRuleValidationException>().WithMessage("*modified*");
    }

    [Fact]
    public void RequestProofChanges_AfterPayment_ThrowsImmutable()
    {
        var order = NegotiationOrder();
        var proof = Upload(order, 1);
        order.ApproveProof(proof.Id, Guid.NewGuid());
        order.FinalizeOrder();
        order.MarkPaid();

        var act = () => order.RequestProofChanges(proof.Id, "Too late for this");

        act.Should().Throw<BusinessRuleValidationException>().WithMessage("*modified*");
        proof.Status.Should().Be(DesignProofStatus.Approved);
    }

    [Fact]
    public void Upload_SupersedesPendingAndApproved_PreservesChangesRequestedHistory()
    {
        var order = NegotiationOrder();
        var v1 = Upload(order, 1);
        order.ApproveProof(v1.Id, Guid.NewGuid());

        var v2 = Upload(order, 2);
        v1.Status.Should().Be(DesignProofStatus.Superseded);
        v2.Status.Should().Be(DesignProofStatus.PendingApproval);

        order.RequestProofChanges(v2.Id, "Wrong shade of green");
        var v3 = Upload(order, 3);

        v2.Status.Should().Be(DesignProofStatus.ChangesRequested,
            "a rejected version keeps its status and reason as the decision history");
        v2.CustomerFeedback.Should().Be("Wrong shade of green");
        v3.Version.Should().Be(3);
        order.Proofs.Select(p => p.Version).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        order.Proofs.Count(p => p.Status == DesignProofStatus.Approved).Should().Be(0,
            "the earlier approval was superseded by the newer upload");
    }

    [Fact]
    public void Approve_SupersededProof_Throws()
    {
        var order = NegotiationOrder();
        var v1 = Upload(order, 1);
        Upload(order, 2);

        var act = () => order.ApproveProof(v1.Id, Guid.NewGuid());

        act.Should().Throw<BusinessRuleValidationException>().WithMessage("*superseded*");
    }
}
