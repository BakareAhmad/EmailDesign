using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Customers;
using DeChimex.Domain.Repositories;
using DeChimex.Domain.ValueObjects;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Customers.Commands;

// ---- Create (admin/sales) ----
public sealed record CreateCustomerCommand(CreateCustomerRequest Request) : ICommand<CustomerDto>;

public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        RuleFor(x => x.Request.FirstName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Request.LastName).NotEmpty().MaximumLength(80);
        // Optional: blank email creates a walk-in guest customer.
        RuleFor(x => x.Request.Email).EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.Request.Email));
        RuleFor(x => x.Request.Phone).MaximumLength(40);
        RuleFor(x => x.Request.Notes).MaximumLength(1000);
    }
}

public sealed class CreateCustomerHandler(ICustomerRepository customers, IUnitOfWork uow)
    : ICommandHandler<CreateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> Handle(CreateCustomerCommand command, CancellationToken ct)
    {
        var r = command.Request;
        if (!string.IsNullOrWhiteSpace(r.Email)
            && await customers.GetByEmailAsync(r.Email, ct) is not null)
            throw new ConflictException("customer.email.exists", $"A customer with email '{r.Email}' already exists.");

        var customer = Domain.Entities.Customer.Create(r.FirstName, r.LastName, r.Email, r.Phone, userId: null, notes: r.Notes);
        await customers.AddAsync(customer, ct);
        await uow.SaveChangesAsync(ct);

        return new CustomerDto(customer.Id, customer.FirstName, customer.LastName, customer.FullName,
            customer.Email?.Value, customer.IsGuest, customer.Phone?.Value, customer.Notes, customer.LifetimeSpending.Amount,
            customer.CreatedDateUtc, Array.Empty<AddressDto>());
    }
}

public sealed record UpdateCustomerProfileCommand(Guid CustomerId, UpdateCustomerProfileRequest Request) : ICommand<Unit>;

public sealed class UpdateCustomerProfileValidator : AbstractValidator<UpdateCustomerProfileCommand>
{
    public UpdateCustomerProfileValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Request.FirstName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Request.LastName).NotEmpty().MaximumLength(80);
    }
}

public sealed class UpdateCustomerProfileHandler(ICustomerRepository customers, IUnitOfWork uow)
    : ICommandHandler<UpdateCustomerProfileCommand, Unit>
{
    public async Task<Unit> Handle(UpdateCustomerProfileCommand c, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(c.CustomerId, ct) ?? throw new NotFoundException("Customer", c.CustomerId);
        customer.UpdateProfile(c.Request.FirstName, c.Request.LastName, c.Request.Phone, c.Request.Notes);
        customers.Update(customer);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record AddCustomerAddressCommand(Guid CustomerId, AddAddressRequest Request) : ICommand<AddressDto>;

public sealed class AddCustomerAddressValidator : AbstractValidator<AddCustomerAddressCommand>
{
    public AddCustomerAddressValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Request.Label).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Request.Street).NotEmpty().MaximumLength(250);
        RuleFor(x => x.Request.City).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Request.State).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Request.Country).NotEmpty().MaximumLength(120); // MED-001
        RuleFor(x => x.Request.PostalCode).MaximumLength(20);          // MED-001
    }
}

public sealed class AddCustomerAddressHandler(ICustomerRepository customers, IUnitOfWork uow)
    : ICommandHandler<AddCustomerAddressCommand, AddressDto>
{
    public async Task<AddressDto> Handle(AddCustomerAddressCommand c, CancellationToken ct)
    {
        var customer = await customers.GetWithAddressesAsync(c.CustomerId, ct) ?? throw new NotFoundException("Customer", c.CustomerId);
        var r = c.Request;
        var addr = customer.AddAddress(r.Label, Address.Create(r.Street, r.City, r.State, r.Country, r.PostalCode), r.IsDefault);
        customers.Update(customer);
        await uow.SaveChangesAsync(ct);
        return new AddressDto(addr.Id, addr.Label, addr.Address.Street, addr.Address.City, addr.Address.State,
            addr.Address.Country, addr.Address.PostalCode, addr.IsDefault);
    }
}

public sealed record RemoveCustomerAddressCommand(Guid CustomerId, Guid AddressId) : ICommand<Unit>;

public sealed class RemoveCustomerAddressHandler(ICustomerRepository customers, IUnitOfWork uow)
    : ICommandHandler<RemoveCustomerAddressCommand, Unit>
{
    public async Task<Unit> Handle(RemoveCustomerAddressCommand c, CancellationToken ct)
    {
        var customer = await customers.GetWithAddressesAsync(c.CustomerId, ct) ?? throw new NotFoundException("Customer", c.CustomerId);
        customer.RemoveAddress(c.AddressId);
        customers.Update(customer);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
