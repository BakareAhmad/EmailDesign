using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

/// <summary>
/// Design reviews are the independent-history file submissions with their own
/// approve/reject decision (unlike versioned, superseding design proofs). These
/// tests pin the contract: submissions coexist, decisions are single-shot and
/// auditable, events carry the customer id for notification routing, and paid
/// orders lock the whole surface.
/// </summary>
public class OrderDesignReviewTests
{
    private static Order NegotiationOrder()
    {
        var order = Order.CreateDraft("ORD-DR-001", Guid.NewGuid());
        order.AddProduct(Guid.NewGuid(), "Flyers", "FLY-1", 50m, 2);
        order.SubmitOrderRequest();
        order.StartDiscussion();
        return order;
    }

    private static OrderDesignReview Upload(Order order, string name = "logo-v1.png", string role = "customer")
        => order.AddDesignReview(
            $"{name}.stored", name, $"/uploads/designreviews/{name}", "image/png",
            1024, Guid.NewGuid(), role, notes: null,
            previewUrl: $"/uploads/designreviews/preview_{name}", previewStoredFileName: $"preview_{name}.stored");

    [Fact]
    public void Create_ValidatesRequiredMetadata()
    {
        var act = () => OrderDesignReview.Create(
            Guid.NewGuid(), "", "logo.png", "/uploads/x.png", "image/png", 10,
            Guid.NewGuid(), "customer");
        act.Should().Throw<BusinessRuleValidationException>();

        var act2 = () => OrderDesignReview.Create(
            Guid.NewGuid(), "a.png", "logo.png", "/uploads/x.png", "image/png", 0,
            Guid.NewGuid(), "customer");
        act2.Should().Throw<BusinessRuleValidationException>();
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("")]
    [InlineData("STAFF")]
    public void Create_RejectsRolesOtherThanCustomerOrStaff(string role)
    {
        var act = () => OrderDesignReview.Create(
            Guid.NewGuid(), "a.png", "logo.png", "/uploads/x.png", "image/png", 10,
            Guid.NewGuid(), role);
        act.Should().Throw<BusinessRuleValidationException>();
    }

    [Fact]
    public void Submissions_MaintainIndependentHistory_NeverSupersede()
    {
        var order = NegotiationOrder();
        var r1 = Upload(order, "artwork-v1.png");
        order.RejectDesignReview(r1.Id, Guid.NewGuid(), "Bleed is missing");

        var r2 = Upload(order, "artwork-v2.png");

        order.DesignReviews.Should().HaveCount(2);
        r1.Status.Should().Be(DesignReviewStatus.Rejected,
            "an older submission keeps its own decision — no supersede rule like proofs");
        r1.RejectionReason.Should().Be("Bleed is missing");
        r2.Status.Should().Be(DesignReviewStatus.Pending);
    }

    [Fact]
    public void Approve_RecordsDeciderAndTimestamp()
    {
        var order = NegotiationOrder();
        var review = Upload(order);
        var staff = Guid.NewGuid();

        order.ApproveDesignReview(review.Id, staff);

        review.Status.Should().Be(DesignReviewStatus.Approved);
        review.ApprovedByUserId.Should().Be(staff);
        review.ApprovedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Approve_AlreadyDecided_Throws()
    {
        var order = NegotiationOrder();
        var review = Upload(order);
        order.ApproveDesignReview(review.Id, Guid.NewGuid());

        var act = () => order.ApproveDesignReview(review.Id, Guid.NewGuid());

        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*pending design reviews*");
    }

    [Fact]
    public void Reject_RequiresReason()
    {
        var order = NegotiationOrder();
        var review = Upload(order);

        var act = () => order.RejectDesignReview(review.Id, Guid.NewGuid(), "   ");

        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*reason*");
        review.Status.Should().Be(DesignReviewStatus.Pending);
    }

    [Fact]
    public void Reject_AlreadyDecided_Throws()
    {
        var order = NegotiationOrder();
        var review = Upload(order);
        order.RejectDesignReview(review.Id, Guid.NewGuid(), "Wrong dimensions");

        var act = () => order.RejectDesignReview(review.Id, Guid.NewGuid(), "Another reason");

        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*pending design reviews*");
        review.RejectionReason.Should().Be("Wrong dimensions", "the first decision is the record");
    }

    [Fact]
    public void Upload_RaisesEventWithCustomerRoleAndFileName()
    {
        var order = NegotiationOrder();
        order.ClearDomainEvents();

        var review = Upload(order, "brand-guide.pdf", "staff");

        var evt = order.DomainEvents.OfType<DesignReviewUploadedEvent>().Should().ContainSingle().Subject;
        evt.OrderId.Should().Be(order.Id);
        evt.CustomerId.Should().Be(order.CustomerId);
        evt.UploadedByUserId.Should().Be(review.UploadedByUserId);
        evt.UploadedByRole.Should().Be("staff");
        evt.OriginalFileName.Should().Be("brand-guide.pdf");
    }

    [Fact]
    public void Decisions_RaiseEventsWithCustomerIdAndReason()
    {
        var order = NegotiationOrder();
        var review = Upload(order);
        order.ClearDomainEvents();

        order.RejectDesignReview(review.Id, Guid.NewGuid(), "Low resolution");

        var rejected = order.DomainEvents.OfType<DesignReviewRejectedEvent>().Should().ContainSingle().Subject;
        rejected.CustomerId.Should().Be(order.CustomerId);
        rejected.Reason.Should().Be("Low resolution");
        rejected.DesignReviewId.Should().Be(review.Id);

        var second = Upload(order, "logo-v2.png");
        order.ClearDomainEvents();
        order.ApproveDesignReview(second.Id, Guid.NewGuid());

        var approved = order.DomainEvents.OfType<DesignReviewApprovedEvent>().Should().ContainSingle().Subject;
        approved.CustomerId.Should().Be(order.CustomerId);
        approved.DesignReviewId.Should().Be(second.Id);
    }

    [Fact]
    public void Upload_AfterPayment_ThrowsImmutable()
    {
        var order = NegotiationOrder();
        order.FinalizeOrder();
        order.MarkPaid();

        var act = () => Upload(order, "too-late.png");

        act.Should().Throw<BusinessRuleValidationException>().WithMessage("*modified*");
    }

    [Fact]
    public void Decisions_AfterPayment_ThrowImmutable()
    {
        var order = NegotiationOrder();
        var review = Upload(order);
        order.FinalizeOrder();
        order.MarkPaid();

        var approve = () => order.ApproveDesignReview(review.Id, Guid.NewGuid());
        var reject = () => order.RejectDesignReview(review.Id, Guid.NewGuid(), "Too late");

        approve.Should().Throw<BusinessRuleValidationException>().WithMessage("*modified*");
        reject.Should().Throw<BusinessRuleValidationException>().WithMessage("*modified*");
        review.Status.Should().Be(DesignReviewStatus.Pending);
    }

    [Fact]
    public void Upload_FromFinalizedUnpaidOrder_IsAllowed()
    {
        var order = NegotiationOrder();
        order.FinalizeOrder();
        order.Status.Should().Be(OrderStatus.PendingPayment);

        var review = Upload(order, "post-finalize.png");

        order.DesignReviews.Should().ContainSingle(r => r.Id == review.Id);
    }

    [Fact]
    public void AwaitDesignApproval_FromFinalizedUnpaidOrder_ReopensDesignGate()
    {
        // §82 lifecycle: a proof uploaded to a finalized-but-unpaid order returns it
        // to AwaitingDesignApproval so the design is approved before payment. This
        // pins the regression where PendingPayment was dropped from the allowed set.
        var order = NegotiationOrder();
        order.FinalizeOrder();

        order.AwaitDesignApproval();

        order.Status.Should().Be(OrderStatus.AwaitingDesignApproval);
    }
}
