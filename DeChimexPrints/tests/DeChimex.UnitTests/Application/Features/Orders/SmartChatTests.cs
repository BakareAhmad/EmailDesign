using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Orders;

/// <summary>
/// Smart-chat domain + application rules: reply integrity, media-or-text sends,
/// first-read-wins receipts, and batched idempotent read beacons.
/// </summary>
public class SmartChatTests
{
    private static Order DraftOrder()
        => Order.CreateDraft("T-CHAT", Guid.NewGuid());

    private static ChatMessageAttachmentInput Attachment(string kind = "image") => new(
        kind, "photo.png", "stored.png", "/uploads/chatmedia/stored.png", "image/png", 12, null);

    [Fact]
    public void AddMessage_ReplyToOwnMessage_PersistsQuote()
    {
        var order = DraftOrder();
        var first = order.AddMessage(Guid.NewGuid(), "staff", "Staff", "Which shade?");

        var reply = order.AddMessage(Guid.NewGuid(), "customer", "Customer", "Victory red", first.Id, null);

        reply.ReplyToMessageId.Should().Be(first.Id);
    }

    [Fact]
    public void AddMessage_ReplyToForeignMessage_Throws()
    {
        var order = DraftOrder();
        order.AddMessage(Guid.NewGuid(), "staff", "Staff", "Hello");

        var act = () => order.AddMessage(Guid.NewGuid(), "customer", "Customer",
            "Hijack", Guid.NewGuid(), null);

        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*quoted message*");
    }

    [Fact]
    public void AddMessage_EmptyBody_WithoutAttachments_Throws()
    {
        var order = DraftOrder();

        var act = () => order.AddMessage(Guid.NewGuid(), "customer", "Customer", "  ", null, null);

        act.Should().Throw<BusinessRuleValidationException>();
    }

    [Fact]
    public void AddMessage_EmptyBody_WithAttachment_IsMediaMessage()
    {
        var order = DraftOrder();

        var message = order.AddMessage(Guid.NewGuid(), "customer", "Customer", "",
            null, [(Attachment().Kind, "photo.png", "stored.png", "/uploads/x.png", "image/png", 12L, (int?)null)]);

        message.Body.Should().BeEmpty();
        message.Attachments.Should().ContainSingle();
    }

    [Fact]
    public void MarkRead_FirstReadWins_LaterOnesAreNoOps()
    {
        var order = DraftOrder();
        var message = order.AddMessage(Guid.NewGuid(), "customer", "Customer", "Hi");
        var firstReader = Guid.NewGuid();

        message.MarkRead(firstReader).Should().BeTrue();
        message.MarkRead(Guid.NewGuid()).Should().BeFalse();
        message.ReadByUserId.Should().Be(firstReader);
        message.DeliveredAtUtc.Should().NotBeNull("read implies delivered");
    }

    [Fact]
    public void MarkDelivered_IsIdempotent()
    {
        var order = DraftOrder();
        var message = order.AddMessage(Guid.NewGuid(), "customer", "Customer", "Hi");

        message.MarkDelivered().Should().BeTrue();
        message.MarkDelivered().Should().BeFalse();
    }

    [Theory]
    [InlineData("  Hello there  ", null, "Hello there")]
    [InlineData("", "image", "Photo")]
    [InlineData("", "voice", "Voice note")]
    [InlineData("", "file", "Attachment")]
    [InlineData("", null, "Message")]
    public void QuoteSnippet_TextOrMediaLabel(string body, string? kind, string expected)
    {
        var kinds = kind is null ? null : new List<string> { kind };

        OrderMessageDto.QuoteSnippet(body, kinds).Should().Be(expected);
    }

    [Fact]
    public void QuoteSnippet_LongText_Truncates()
    {
        OrderMessageDto.QuoteSnippet(new string('x', 200), null)
            .Should().HaveLength(141).And.EndWith("…");
    }

    [Fact]
    public void SendValidator_RejectsOversizedOrUnknownAttachments()
    {
        var sut = new SendOrderMessageValidator();
        var tooMany = Enumerable.Range(0, 7).Select(_ => Attachment()).ToList();

        sut.Validate(new SendOrderMessageCommand(Guid.NewGuid(),
            new SendOrderMessageRequest("", null, tooMany), "customer")).IsValid.Should().BeFalse();

        sut.Validate(new SendOrderMessageCommand(Guid.NewGuid(),
            new SendOrderMessageRequest("Hi", null, [Attachment() with { Kind = " hologram " }]), "customer"))
            .IsValid.Should().BeFalse();

        // Empty text alone passes the validator — the "body or attachment" rule
        // lives in the domain, which sees the full message.
        sut.Validate(new SendOrderMessageCommand(Guid.NewGuid(),
            new SendOrderMessageRequest("", null, null), "customer")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task MarkRead_MarksOnlyOthersMessages_AndPushesOnce()
    {
        var order = DraftOrder();
        var customerMsg = order.AddMessage(Guid.NewGuid(), "customer", "Customer", "Question");
        var staffMsg = order.AddMessage(Guid.NewGuid(), "staff", "Staff", "Answer");
        var orders = Substitute.For<IOrderRepository>();
        orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        var chat = Substitute.For<IOrderChatPusher>();
        var uow = Substitute.For<IUnitOfWork>();
        var sut = new MarkMessagesReadHandler(orders, Substitute.For<ICurrentUser>(), chat, uow);

        var read = await sut.Handle(
            new MarkMessagesReadCommand(order.Id, [customerMsg.Id, staffMsg.Id, Guid.NewGuid()], "staff"),
            CancellationToken.None);

        read.Should().BeEquivalentTo([customerMsg.Id], "own + unknown ids are ignored");
        customerMsg.ReadAtUtc.Should().NotBeNull();
        staffMsg.ReadAtUtc.Should().BeNull("a reader never reads their own sends");
        await chat.Received(1).PushOrderEventAsync(order.Id, "orderMessagesRead",
            Arg.Any<object>(), Arg.Any<CancellationToken>());

        // A repeat beacon changes nothing and pushes nothing.
        var reread = await sut.Handle(
            new MarkMessagesReadCommand(order.Id, [customerMsg.Id], "staff"), CancellationToken.None);
        reread.Should().BeEmpty();
        await chat.Received(1).PushOrderEventAsync(order.Id, Arg.Any<string>(),
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendHandler_DeliveredReflectsRecipientPresence()
    {
        var orders = Substitute.For<IOrderRepository>();
        var presence = Substitute.For<IOrderPresenceTracker>();
        var chat = Substitute.For<IOrderChatPusher>();
        var uow = Substitute.For<IUnitOfWork>();
        var sut = new SendOrderMessageHandler(orders, Substitute.For<ICustomerRepository>(),
            Substitute.For<IUserRepository>(), Substitute.For<ICurrentUser>(), chat, presence, uow);

        var online = DraftOrder();
        orders.GetWithWorkspaceAsync(online.Id, Arg.Any<CancellationToken>()).Returns(online);
        presence.IsRecipientConnected(online.Id, "customer").Returns(true);
        var dtoOnline = await sut.Handle(new SendOrderMessageCommand(online.Id,
            new SendOrderMessageRequest("Hi", null, null), "customer"), CancellationToken.None);
        dtoOnline.DeliveredAtUtc.Should().NotBeNull();

        var offline = DraftOrder();
        orders.GetWithWorkspaceAsync(offline.Id, Arg.Any<CancellationToken>()).Returns(offline);
        presence.IsRecipientConnected(offline.Id, "customer").Returns(false);
        var dtoOffline = await sut.Handle(new SendOrderMessageCommand(offline.Id,
            new SendOrderMessageRequest("Hi", null, null), "customer"), CancellationToken.None);
        dtoOffline.DeliveredAtUtc.Should().BeNull();
    }
}
