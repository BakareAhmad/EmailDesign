using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

public class OrderSalesAssignmentTests
{
    private readonly Guid _customerId = Guid.NewGuid();

    private Order CreateRequest()
    {
        var order = Order.CreateDraft("DCX-TEST-001", _customerId);
        order.AddProduct(Guid.NewGuid(), "Custom Jersey", "SKU-1", 15000m, 2);
        order.SubmitOrderRequest();
        return order;
    }

    [Fact]
    public void NewOrder_StartsUnassigned_WithActiveConversation()
    {
        var order = CreateRequest();

        order.AssignedSalesUserId.Should().BeNull();
        order.AssignedSalesName.Should().BeNull();
        order.ConversationState.Should().Be(OrderConversationState.Active);
    }

    [Fact]
    public void AssignSalesStaff_SetsOwner_AndRaisesAssignedEvent()
    {
        var order = CreateRequest();
        var salesId = Guid.NewGuid();

        order.AssignSalesStaff(salesId, "Sarah Johnson");

        order.AssignedSalesUserId.Should().Be(salesId);
        order.AssignedSalesName.Should().Be("Sarah Johnson");
        var raised = order.DomainEvents.OfType<SalesAssignedEvent>().Should().ContainSingle().Subject;
        raised.NewSalesUserId.Should().Be(salesId);
        raised.PreviousSalesUserId.Should().BeNull();
        raised.IsReassignment.Should().BeFalse();
    }

    [Fact]
    public void AssignSalesStaff_Reassign_FlagsIsReassignment_WithPreviousOwner()
    {
        var order = CreateRequest();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        order.AssignSalesStaff(first, "Sarah");

        order.AssignSalesStaff(second, "David");

        order.AssignedSalesUserId.Should().Be(second);
        var raised = order.DomainEvents.OfType<SalesAssignedEvent>().Last();
        raised.PreviousSalesUserId.Should().Be(first);
        raised.IsReassignment.Should().BeTrue();
    }

    [Fact]
    public void AssignSalesStaff_SameOwner_IsNotAReassignment()
    {
        var order = CreateRequest();
        var salesId = Guid.NewGuid();
        order.AssignSalesStaff(salesId, "Sarah");

        order.AssignSalesStaff(salesId, "Sarah");

        order.DomainEvents.OfType<SalesAssignedEvent>().Last().IsReassignment.Should().BeFalse();
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "Sarah")]
    public void AssignSalesStaff_InvalidUser_Throws(string userId, string name)
    {
        var order = CreateRequest();

        var act = () => order.AssignSalesStaff(Guid.Parse(userId), name);

        act.Should().Throw<BusinessRuleValidationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AssignSalesStaff_BlankName_Throws(string? name)
    {
        var order = CreateRequest();

        var act = () => order.AssignSalesStaff(Guid.NewGuid(), name!);

        act.Should().Throw<BusinessRuleValidationException>();
    }

    [Fact]
    public void Conversation_MarksWaiting_ForEachSide()
    {
        var order = CreateRequest();

        order.MarkConversationWaitingForStaff();
        order.ConversationState.Should().Be(OrderConversationState.WaitingForStaff);

        order.MarkConversationWaitingForCustomer();
        order.ConversationState.Should().Be(OrderConversationState.WaitingForCustomer);
    }

    [Fact]
    public void Conversation_Resolved_BlocksFurtherFlips()
    {
        var order = CreateRequest();
        order.ResolveConversation();

        order.MarkConversationWaitingForStaff();
        order.MarkConversationWaitingForCustomer();

        order.ConversationState.Should().Be(OrderConversationState.Resolved);
    }

    [Fact]
    public void FinalizeOrder_ResolvesConversation()
    {
        var order = CreateRequest();
        order.MarkConversationWaitingForStaff();

        order.FinalizeOrder();

        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.ConversationState.Should().Be(OrderConversationState.Resolved);
    }

    [Fact]
    public void Cancel_ResolvesConversation()
    {
        var order = CreateRequest();

        order.Cancel("customer changed mind");

        order.ConversationState.Should().Be(OrderConversationState.Resolved);
    }

    [Fact]
    public void Refund_ResolvesConversation()
    {
        var order = Order.CreateDraft("DCX-TEST-002", _customerId);
        order.AddProduct(Guid.NewGuid(), "Mug", "SKU-2", 5000m, 1);
        order.PlaceOrder();
        order.MarkPaid();

        order.Refund();

        order.ConversationState.Should().Be(OrderConversationState.Resolved);
    }

    [Fact]
    public void MarkCompleted_AndMarkDelivered_ResolveConversation()
    {
        var order = Order.CreateDraft("DCX-TEST-003", _customerId);
        order.AddProduct(Guid.NewGuid(), "Mug", "SKU-3", 5000m, 1);
        order.PlaceOrder();
        order.MarkPaid();
        order.StartProduction();
        order.MarkReady();

        order.MarkCompleted();
        order.ConversationState.Should().Be(OrderConversationState.Resolved);

        order.MarkDelivered();
        order.ConversationState.Should().Be(OrderConversationState.Resolved);
    }

    [Theory]
    [InlineData(OrderStatus.AwaitingReview, true)]
    [InlineData(OrderStatus.InDiscussion, true)]
    [InlineData(OrderStatus.RequirementsConfirmed, true)]
    [InlineData(OrderStatus.AwaitingDesignApproval, true)]
    [InlineData(OrderStatus.Draft, false)]
    [InlineData(OrderStatus.PendingPayment, false)]
    [InlineData(OrderStatus.Paid, false)]
    [InlineData(OrderStatus.InProduction, false)]
    [InlineData(OrderStatus.Ready, false)]
    [InlineData(OrderStatus.Completed, false)]
    [InlineData(OrderStatus.Delivered, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Refunded, false)]
    public void IsActiveSalesWork_ClassifiesStatuses(OrderStatus status, bool expected)
    {
        Order.IsActiveSalesWork(status).Should().Be(expected);
    }
}
