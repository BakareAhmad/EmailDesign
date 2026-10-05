using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Services;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Services.Commands;

// ---- Create ----
public sealed record CreateServiceCommand(CreateServiceRequest Request) : ICommand<ServiceDto>;

public sealed class CreateServiceValidator : AbstractValidator<CreateServiceCommand>
{
    public CreateServiceValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.Description).MaximumLength(4000);
        RuleFor(x => x.Request.BasePrice).GreaterThanOrEqualTo(0);
    }
}

public sealed class CreateServiceHandler(IServiceRepository services, IUnitOfWork uow, DeChimex.Application.Common.Interfaces.ICacheService cache)
    : ICommandHandler<CreateServiceCommand, ServiceDto>
{
    public async Task<ServiceDto> Handle(CreateServiceCommand command, CancellationToken ct)
    {
        var r = command.Request;

        // Auto-assign a curated icon color when the caller didn't supply one.
        // Prefer the palette colour least used by existing services so a set of
        // services gains natural variety instead of all landing on one accent.
        var iconColor = r.IconColor;
        if (string.IsNullOrWhiteSpace(iconColor))
        {
            var existing = await services.ListAsync(ct);
            iconColor = ServiceIconPalette.Pick(existing.Select(s => s.IconColor).ToList());
        }

        var service = Service.Create(r.Name, r.Description, r.BasePrice, iconColor, r.IsFeatured);
        await services.AddAsync(service, ct);
        await uow.SaveChangesAsync(ct);
        foreach (var key in DeChimex.Application.Common.CacheKeys.ServiceLists) await cache.RemoveAsync(key, ct); // MED-004
        return new ServiceDto(service.Id, service.Name, service.Slug.Value, service.Description,
            service.BasePrice.Amount, service.BasePrice.Currency, service.IconColor, service.IsActive, service.IsFeatured);
    }
}

// ---- Update ----
public sealed record UpdateServiceCommand(Guid Id, UpdateServiceRequest Request) : ICommand<Unit>;

public sealed class UpdateServiceValidator : AbstractValidator<UpdateServiceCommand>
{
    public UpdateServiceValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.BasePrice).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpdateServiceHandler(IServiceRepository services, IUnitOfWork uow, DeChimex.Application.Common.Interfaces.ICacheService cache)
    : ICommandHandler<UpdateServiceCommand, Unit>
{
    public async Task<Unit> Handle(UpdateServiceCommand command, CancellationToken ct)
    {
        var service = await services.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Service", command.Id);
        var r = command.Request;

        // Preserve the existing accent when an edit doesn't provide a new one
        // (the storefront renders whatever is stored, so we never blank it).
        var iconColor = string.IsNullOrWhiteSpace(r.IconColor)
            ? (string.IsNullOrWhiteSpace(service.IconColor) ? ServiceIconPalette.DefaultColor : service.IconColor)
            : r.IconColor;

        service.Update(r.Name, r.Description, r.BasePrice, iconColor, r.IsFeatured);
        if (r.IsActive is bool active)
        {
            if (active) service.Activate(); else service.Deactivate();
        }
        services.Update(service);
        await uow.SaveChangesAsync(ct);
        foreach (var key in DeChimex.Application.Common.CacheKeys.ServiceLists) await cache.RemoveAsync(key, ct); // MED-004
        return Unit.Value;
    }
}

// ---- Delete ----
public sealed record DeleteServiceCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteServiceHandler(IServiceRepository services, IUnitOfWork uow, DeChimex.Application.Common.Interfaces.ICacheService cache)
    : ICommandHandler<DeleteServiceCommand, Unit>
{
    public async Task<Unit> Handle(DeleteServiceCommand command, CancellationToken ct)
    {
        var service = await services.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Service", command.Id);
        services.Remove(service);
        await uow.SaveChangesAsync(ct);
        foreach (var key in DeChimex.Application.Common.CacheKeys.ServiceLists) await cache.RemoveAsync(key, ct); // MED-004
        return Unit.Value;
    }
}
