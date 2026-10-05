using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Procurement.Commands;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.ProcurementCommands;

/// <summary>Unit tests for DeleteProcurement: only requested/approved records may be deleted.</summary>
public class DeleteProcurementHandlerTests
{
    private readonly IProcurementRepository _procurements = Substitute.For<IProcurementRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly DeleteProcurementHandler _sut;

    public DeleteProcurementHandlerTests() => _sut = new DeleteProcurementHandler(_procurements, _uow);

    private static Procurement WithItem(string reference)
    {
        var p = Procurement.Request(reference);
        p.AddItem(Guid.NewGuid(), "Tote Bag", 10);
        return p;
    }

    [Fact]
    public async Task Handle_WhenMissing_ThrowsNotFound()
    {
        _procurements.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Procurement?)null);

        var act = () => _sut.Handle(new DeleteProcurementCommand(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRequested_Removes()
    {
        var p = WithItem("PUR-1");
        _procurements.GetByIdAsync(p.Id, Arg.Any<CancellationToken>()).Returns(p);

        await _sut.Handle(new DeleteProcurementCommand(p.Id), CancellationToken.None);

        _procurements.Received(1).Remove(p);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenApproved_Removes()
    {
        var p = WithItem("PUR-2");
        p.Approve("admin@dechimex.com");
        _procurements.GetByIdAsync(p.Id, Arg.Any<CancellationToken>()).Returns(p);

        await _sut.Handle(new DeleteProcurementCommand(p.Id), CancellationToken.None);

        _procurements.Received(1).Remove(p);
    }

    [Fact]
    public async Task Handle_WhenPurchased_ThrowsAndKeepsRecord()
    {
        var p = WithItem("PUR-3");
        p.Approve("admin@dechimex.com");
        p.MarkPurchased("Nile Fabrics");
        _procurements.GetByIdAsync(p.Id, Arg.Any<CancellationToken>()).Returns(p);

        var act = () => _sut.Handle(new DeleteProcurementCommand(p.Id), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleValidationException>().WithMessage("*stock*");
        _procurements.DidNotReceive().Remove(Arg.Any<Procurement>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
