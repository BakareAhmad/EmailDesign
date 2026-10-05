using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Payments;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Orders;

/// <summary>
/// Live-workspace pushes: every workspace mutation broadcasts its event to the
/// order's SignalR group AFTER the change is committed, and requirements
/// confirmation notifies the waiting customer directly.
/// </summary>
public class WorkspaceRealtimeTests
{
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IOrderChatPusher _chat = Substitute.For<IOrderChatPusher>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private static Order DraftOrder(Guid? customerId = null)
    {
        var order = Order.CreateDraft("T-REALTIME", customerId ?? Guid.NewGuid());
        order.AddProduct(Guid.NewGuid(), "Widget", "W-1", 100m, 1);
        return order;
    }

    private static UploadOrderFileRequest FileRequest() => new(
        "stored.bin", "brief.pdf", "/uploads/brief.pdf", "application/pdf", 42, "Reference");

    [Fact]
    public async Task UploadFile_PushesOrderFile_AfterCommit_WithTimelineEntry()
    {
        var order = DraftOrder();
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        var sequence = new List<string>();
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1)).AndDoes(_ => sequence.Add("save"));
        _chat.PushOrderEventAsync(order.Id, "orderFile", Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => sequence.Add("push"));

        var sut = new UploadOrderFileHandler(_orders, _currentUser, _chat, _uow);
        var dto = await sut.Handle(new UploadOrderFileCommand(order.Id, FileRequest(), "customer"), CancellationToken.None);

        dto.OriginalFileName.Should().Be("brief.pdf");
        order.Timeline.Should().ContainSingle(t => t.Type == "file_uploaded");
        sequence.Should().Equal("save", "push");
    }

    [Fact]
    public async Task DeleteFile_PushesOrderFileRemoved()
    {
        var order = DraftOrder();
        var file = order.AddFile("s.bin", "old.pdf", "/uploads/old.pdf", "application/pdf", 9, null, null, "customer");
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        var sut = new DeleteOrderFileHandler(_orders, _currentUser, _chat, _uow);
        await sut.Handle(new DeleteOrderFileCommand(order.Id, file.Id, "staff"), CancellationToken.None);

        await _chat.Received(1).PushOrderEventAsync(order.Id, "orderFileRemoved",
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadProof_PushesOrderProof()
    {
        var order = DraftOrder();
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        var sut = new UploadDesignProofHandler(_orders, _currentUser, _chat, _uow);
        var dto = await sut.Handle(new UploadDesignProofCommand(order.Id,
            new UploadDesignProofRequest("p.bin", "proof.png", "/uploads/proof.png", "image/png", "First draft")),
            CancellationToken.None);

        dto.Version.Should().Be(1);
        await _chat.Received(1).PushOrderEventAsync(order.Id, "orderProof",
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveProof_PushesOrderProofStatus_Approved()
    {
        var userId = Guid.NewGuid();
        var customer = Customer.Create("Ada", "Buyer", "ada@example.com", userId: userId);
        var order = DraftOrder(customer.Id);
        var proof = order.AddProof("p.bin", "proof.png", "/uploads/proof.png", "image/png", null);
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        _currentUser.UserId.Returns(userId);
        _customers.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(customer);

        var sut = new ApproveDesignProofHandler(_orders, _customers, _currentUser, _chat, _uow);
        await sut.Handle(new ApproveDesignProofCommand(order.Id, proof.Id), CancellationToken.None);

        await _chat.Received(1).PushOrderEventAsync(order.Id, "orderProofStatus",
            Arg.Is<object>(p => p.ToString()!.Contains("Approved")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestChanges_PushesOrderProofStatus_ChangesRequested()
    {
        var userId = Guid.NewGuid();
        var customer = Customer.Create("Ada", "Buyer", "ada@example.com", userId: userId);
        var order = DraftOrder(customer.Id);
        var proof = order.AddProof("p.bin", "proof.png", "/uploads/proof.png", "image/png", null);
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        _currentUser.UserId.Returns(userId);
        _customers.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(customer);

        var sut = new RequestDesignChangesHandler(_orders, _customers, _currentUser, _chat, _uow);
        await sut.Handle(new RequestDesignChangesCommand(order.Id, proof.Id, "Make it pop"), CancellationToken.None);

        proof.Status.Should().Be(DesignProofStatus.ChangesRequested);
        await _chat.Received(1).PushOrderEventAsync(order.Id, "orderProofStatus",
            Arg.Is<object>(p => p.ToString()!.Contains("ChangesRequested")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Finalize_PushesOrderStatus()
    {
        var order = DraftOrder();
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        var sut = new FinalizeOrderHandler(_orders, _currentUser, _chat, _uow);
        await sut.Handle(new FinalizeOrderCommand(order.Id, new FinalizeOrderRequest(
            new List<OrderLinePricingInput>(), new List<OrderCustomLineInput>
            {
                new("Design prep", 500m, 1)
            }, 0m, 0m, 0m, null)), CancellationToken.None);

        order.Status.Should().Be(OrderStatus.PendingPayment);
        await _chat.Received(1).PushOrderEventAsync(order.Id, "orderStatus",
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstMessage_StartsDiscussion_Naturally()
    {
        // Natural workflow: first message automatically starts discussion, making
        // explicit "Start Discussion" action redundant.
        var customer = Customer.Create("Ada", "Buyer", "ada@example.com", userId: Guid.NewGuid());
        var order = DraftOrder(customer.Id);
        order.SubmitOrderRequest(); // Draft -> AwaitingReview
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        _currentUser.UserId.Returns(Guid.NewGuid());
        _customers.GetByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(customer);

        var sut = new SendOrderMessageHandler(_orders, _customers, Substitute.For<IUserRepository>(),
            _currentUser, _chat, Substitute.For<IOrderPresenceTracker>(), _uow);
        
        // First message should trigger discussion start
        await sut.Handle(new SendOrderMessageCommand(order.Id,
            new SendOrderMessageRequest("Hello, let's discuss"), "customer"), CancellationToken.None);

        order.Status.Should().Be(OrderStatus.InDiscussion);
        order.Messages.Should().HaveCount(1);
        order.Timeline.Should().Contain(t => t.Type == "discussion");
        await _chat.Received(1).PushMessageAsync(order.Id, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Finalize_ConfirmsRequirements_Naturally()
    {
        // Natural workflow: Finalizing implicitly confirms requirements.
        var order = DraftOrder();
        order.SubmitOrderRequest();
        order.StartDiscussion(); // Move to discussion first
        _orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        var sut = new FinalizeOrderHandler(_orders, _currentUser, _chat, _uow);
        await sut.Handle(new FinalizeOrderCommand(order.Id, new FinalizeOrderRequest(
            new List<OrderLinePricingInput>(), new List<OrderCustomLineInput>
            {
                new("Design prep", 500m, 1)
            }, 0m, 0m, 0m, null)), CancellationToken.None);

        // Finalization should have confirmed requirements
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.Timeline.Should().Contain(t => t.Type == "confirmed");
        await _chat.Received(1).PushOrderEventAsync(order.Id, "orderStatus",
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateStatus_PushesOrderStatus()
    {
        var order = DraftOrder();
        order.PlaceOrder();
        order.MarkPaid();
        order.StartProduction();
        _orders.GetWithLinesAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        var inventories = Substitute.For<IInventoryRepository>();

        var sut = new UpdateOrderStatusHandler(_orders, inventories, _notifications, _chat, _currentUser, _uow);
        await sut.Handle(new UpdateOrderStatusCommand(order.Id, "Ready"), CancellationToken.None);

        order.Status.Should().Be(OrderStatus.Ready);
        await _chat.Received(1).PushOrderEventAsync(order.Id, "orderStatus",
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmPaidOrder_ReturnsTrueOnce_ThenFalseWhenAlreadyPaid()
    {
        var order = DraftOrder();
        order.PlaceOrder();
        var orders = Substitute.For<IOrderRepository>();
        orders.GetWithLinesAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        var invoices = Substitute.For<IInvoiceRepository>();
        invoices.GetByOrderIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns((Invoice?)null);
        var payments = Substitute.For<IPaymentRepository>();
        payments.GetByOrderIdAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Payment>());
        _customers.GetByIdAsync(order.CustomerId, Arg.Any<CancellationToken>())
            .Returns((Customer?)null);
        var references = Substitute.For<IReferenceGenerator>();
        references.NextInvoiceNumberAsync(Arg.Any<CancellationToken>()).Returns("INV-1");
        references.NewShareToken().Returns("tok");

        var sut = new PaymentConfirmationService(orders, invoices,
            Substitute.For<IReceiptRepository>(), payments, _customers,
            Substitute.For<IOrderFulfilmentService>(), references, _notifications,
            Substitute.For<IEmailQueue>(), Substitute.For<IEmailTemplateRenderer>(),
            Substitute.For<IAppUrlProvider>(), _currentUser);

        (await sut.ConfirmPaidOrderAsync(order.Id, CancellationToken.None)).Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        (await sut.ConfirmPaidOrderAsync(order.Id, CancellationToken.None)).Should().BeFalse();
    }
}
