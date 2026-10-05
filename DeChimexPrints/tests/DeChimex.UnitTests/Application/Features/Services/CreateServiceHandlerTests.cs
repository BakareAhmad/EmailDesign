using DeChimex.Application.Common;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Services.Commands;
using DeChimex.Contracts.Services;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Services;

/// <summary>
/// Tests that creating a service auto-assigns a curated icon colour when none is
/// supplied, and that the assigned colour is drawn from the palette (never a random
/// off-palette value). Reuses the existing handler with its dependencies mocked.
/// </summary>
public class CreateServiceHandlerTests
{
    private readonly IServiceRepository _services = Substitute.For<IServiceRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly CreateServiceHandler _sut;

    public CreateServiceHandlerTests() => _sut = new CreateServiceHandler(_services, _uow, _cache);

    [Fact]
    public async Task Handle_WithoutIconColor_AutoAssignsAPaletteColor()
    {
        _services.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<Service>());
        var command = new CreateServiceCommand(new CreateServiceRequest(
            "Branded Mugs", "Custom printed mugs", 1500m, IconColor: null, IsFeatured: false));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IconColor.Should().NotBeNullOrEmpty();
        ServiceIconPalette.IsPaletteColor(result.IconColor).Should().BeTrue();
        await _services.Received(1).AddAsync(
            Arg.Is<Service>(s => s.IconColor == result.IconColor), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithoutColor_AvoidsTheAccentOtherServicesUse()
    {
        // An existing service already uses the indigo accent; the new one should be
        // given a different palette colour so the storefront shows variety.
        _services.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<Service>
        {
            Service.Create("Existing", "desc", 1000m, "#818CF8")
        });
        var command = new CreateServiceCommand(new CreateServiceRequest(
            "Hoodies", "Printed hoodies", 2500m, IconColor: null, IsFeatured: false));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IconColor.Should().NotBe("#818CF8");
        ServiceIconPalette.IsPaletteColor(result.IconColor).Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithExplicitIconColor_KeepsIt_AndDoesNotReassign()
    {
        _services.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<Service>());
        var command = new CreateServiceCommand(new CreateServiceRequest(
            "Flasks", "Engraved flasks", 3000m, "#34D399", IsFeatured: false));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IconColor.Should().Be("#34D399");
        // ListAsync must not be needed when an explicit colour is supplied.
        await _services.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }
}
