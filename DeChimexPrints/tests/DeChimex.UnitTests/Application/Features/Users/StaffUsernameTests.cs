using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Orders.Commands;
using DeChimex.Application.Features.Users;
using DeChimex.Contracts.Auth;
using DeChimex.Contracts.Orders;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using DeChimex.Shared.Constants;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Users;

/// <summary>
/// Staff usernames: validation/normalisation rules, staff creation + change
/// flows (uniqueness, customer rejection, chat-history propagation), and the
/// rule that staff chat messages are labelled with the username — never the
/// staff member's name or email.
/// </summary>
public class StaffUsernameTests
{
    [Theory]
    [InlineData("shakur", "shakur")]
    [InlineData("Shakur", "shakur")]
    [InlineData("  Night.Owl_9-x ", "night.owl_9-x")]
    [InlineData("abc", "abc")]
    public void Username_Create_NormalisesValidNames(string input, string expected)
        => Username.Create(input).Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("has space")]
    [InlineData("no@email")]
    [InlineData("unicode-é")]
    public void Username_Create_RejectsInvalidNames(string? input)
        => Assert.Throws<BusinessRuleValidationException>(() => Username.Create(input!));

    [Fact]
    public void Username_Create_RejectsTooLong()
        => Assert.Throws<BusinessRuleValidationException>(() => Username.Create(new string('a', 31)));

    [Theory]
    [InlineData("staff")]
    [InlineData("Staff")]
    [InlineData("SUPPORT")]
    [InlineData("admin")]
    [InlineData("customer")]
    [InlineData("system")]
    public void Username_Create_RejectsReservedNames(string input)
        => Assert.Throws<BusinessRuleValidationException>(() => Username.Create(input))
            .Message.Should().Contain("reserved");

    [Theory]
    [InlineData("John.Doe@example.com", "john.doe")]
    [InlineData("a@example.com", "auser")]
    [InlineData("staff@example.com", "staff01")]
    [InlineData("x+y@example.com", "x.y")]
    [InlineData("not-an-email", "not-an-email")]
    public void Username_SuggestFromEmail_ProducesValidBase(string email, string expected)
    {
        var suggestion = Username.SuggestFromEmail(email);

        suggestion.Should().Be(expected);
        Username.Create(suggestion).Value.Should().Be(suggestion);
    }

    [Fact]
    public void CreateStaffValidator_AcceptsMixedCase_RejectsReservedAndShort()
    {
        var sut = new CreateStaffUserValidator();
        var roleId = Guid.NewGuid();

        sut.Validate(new CreateStaffUserCommand(
            new CreateStaffUserRequest("A", "B", "e@x.com", "StrongPass1", "Mixed.Case_9", [roleId]))).IsValid
            .Should().BeTrue("mixed case normalises to lower-case");

        sut.Validate(new CreateStaffUserCommand(
            new CreateStaffUserRequest("A", "B", "e@x.com", "StrongPass1", "support", [roleId]))).IsValid
            .Should().BeFalse("reserved names are rejected");

        sut.Validate(new CreateStaffUserCommand(
            new CreateStaffUserRequest("A", "B", "e@x.com", "StrongPass1", "ab", [roleId]))).IsValid
            .Should().BeFalse("too-short names are rejected");
    }

    [Fact]
    public async Task CreateStaff_DuplicateUsername_ThrowsConflict()
    {
        var users = Substitute.For<IUserRepository>();
        users.EmailExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        users.UsernameExistsAsync("shakur", null, Arg.Any<CancellationToken>()).Returns(true);
        var sut = new CreateStaffUserHandler(users, Substitute.For<IRoleRepository>(),
            Substitute.For<IPasswordHasher>(), Substitute.For<IUnitOfWork>());

        var act = () => sut.Handle(new CreateStaffUserCommand(
            new CreateStaffUserRequest("A", "B", "e@x.com", "StrongPass1", "shakur", [Guid.NewGuid()])),
            CancellationToken.None);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already taken*");
    }

    [Fact]
    public async Task SetUsername_RenamesUser_AndPropagatesToChatHistory()
    {
        var user = User.CreateStaff("s@example.com", "hash", "Ada", "Sales", "oldname");
        var role = Role.Create(RoleNames.SalesStaff, null, isSystemRole: true);
        user.AssignRole(role.Id);
        var users = Substitute.For<IUserRepository>();
        users.GetWithRolesAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        users.UsernameExistsAsync("newname", user.Id, Arg.Any<CancellationToken>()).Returns(false);
        var roles = Substitute.For<IRoleRepository>();
        roles.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([role]);
        var orders = Substitute.For<IOrderRepository>();
        var sut = new SetUsernameHandler(users, roles, orders, Substitute.For<IUnitOfWork>());

        await sut.Handle(new SetUsernameCommand(user.Id, "NewName"), CancellationToken.None);

        user.Username!.Value.Should().Be("newname", "usernames normalise to lower-case");
        await orders.Received(1).RenameStaffAuthorAsync(user.Id, "newname", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetUsername_Duplicate_ThrowsConflict()
    {
        var user = User.CreateStaff("s@example.com", "hash", "Ada", "Sales", "oldname");
        var role = Role.Create(RoleNames.SalesStaff, null, isSystemRole: true);
        user.AssignRole(role.Id);
        var users = Substitute.For<IUserRepository>();
        users.GetWithRolesAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        users.UsernameExistsAsync("taken", user.Id, Arg.Any<CancellationToken>()).Returns(true);
        var roles = Substitute.For<IRoleRepository>();
        roles.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([role]);
        var sut = new SetUsernameHandler(users, roles,
            Substitute.For<IOrderRepository>(), Substitute.For<IUnitOfWork>());

        var act = () => sut.Handle(new SetUsernameCommand(user.Id, "taken"), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task SetUsername_CustomerAccount_IsRejected()
    {
        var user = User.Register("c@example.com", "hash", "Kay", "Customer", "token");
        var role = Role.Create(RoleNames.Customer, null, isSystemRole: true);
        user.AssignRole(role.Id);
        var users = Substitute.For<IUserRepository>();
        users.GetWithRolesAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var roles = Substitute.For<IRoleRepository>();
        roles.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([role]);
        var sut = new SetUsernameHandler(users, roles,
            Substitute.For<IOrderRepository>(), Substitute.For<IUnitOfWork>());

        var act = () => sut.Handle(new SetUsernameCommand(user.Id, "nope"), CancellationToken.None);

        (await act.Should().ThrowAsync<BusinessRuleValidationException>())
            .WithMessage("*Customer accounts*");
    }

    [Fact]
    public async Task SendOrderMessage_Staff_LabelsMessageWithUsername()
    {
        var staff = User.CreateStaff("ada.sales@example.com", "hash", "Adaeze", "Okafor", "shakur");
        var order = Order.CreateDraft("T-USER", Guid.NewGuid());
        var orders = Substitute.For<IOrderRepository>();
        orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(staff.Id, Arg.Any<CancellationToken>()).Returns(staff);
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(staff.Id);
        currentUser.Email.Returns("ada.sales@example.com");
        var sut = new SendOrderMessageHandler(orders, Substitute.For<ICustomerRepository>(), users,
            currentUser, Substitute.For<IOrderChatPusher>(), Substitute.For<IOrderPresenceTracker>(),
            Substitute.For<IUnitOfWork>());

        var dto = await sut.Handle(new SendOrderMessageCommand(order.Id,
            new SendOrderMessageRequest("Hello", null, null), "staff"), CancellationToken.None);

        dto.AuthorName.Should().Be("shakur");
        dto.AuthorName.Should().NotContain("ada.sales@example.com");
        dto.AuthorName.Should().NotContain("Adaeze");
    }

    [Fact]
    public async Task SendOrderMessage_UnknownStaff_FallsBackToGenericLabel()
    {
        var order = Order.CreateDraft("T-USER", Guid.NewGuid());
        var orders = Substitute.For<IOrderRepository>();
        orders.GetWithWorkspaceAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(Guid.NewGuid());
        currentUser.Email.Returns("ghost@example.com");
        var sut = new SendOrderMessageHandler(orders, Substitute.For<ICustomerRepository>(), users,
            currentUser, Substitute.For<IOrderChatPusher>(), Substitute.For<IOrderPresenceTracker>(),
            Substitute.For<IUnitOfWork>());

        var dto = await sut.Handle(new SendOrderMessageCommand(order.Id,
            new SendOrderMessageRequest("Hello", null, null), "staff"), CancellationToken.None);

        dto.AuthorName.Should().Be("Staff", "the fallback is generic — never the email");
    }

    [Fact]
    public void RenameAuthor_ValidatesInput()
    {
        var order = Order.CreateDraft("T-USER", Guid.NewGuid());
        var message = order.AddMessage(Guid.NewGuid(), "staff", "old", "Hi");

        message.RenameAuthor("newname");
        message.AuthorName.Should().Be("newname");

        Assert.Throws<BusinessRuleValidationException>(() => message.RenameAuthor("  "));
        Assert.Throws<BusinessRuleValidationException>(() => message.RenameAuthor(new string('x', 161)));
    }
}
