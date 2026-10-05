using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Payments;

namespace DeChimex.Application.Features.Payments.Queries;

public sealed record GetPaymentHistoryQuery(Guid CustomerId) : IQuery<IReadOnlyList<PaymentDto>>;
public sealed class GetPaymentHistoryHandler(IPaymentQueries queries)
    : IQueryHandler<GetPaymentHistoryQuery, IReadOnlyList<PaymentDto>>
{
    public Task<IReadOnlyList<PaymentDto>> Handle(GetPaymentHistoryQuery q, CancellationToken ct)
        => queries.GetForCustomerAsync(q.CustomerId, ct);
}

public sealed record GetInvoiceByOrderQuery(Guid OrderId) : IQuery<InvoiceDto?>;
public sealed class GetInvoiceByOrderHandler(IPaymentQueries queries)
    : IQueryHandler<GetInvoiceByOrderQuery, InvoiceDto?>
{
    public Task<InvoiceDto?> Handle(GetInvoiceByOrderQuery q, CancellationToken ct)
        => queries.GetInvoiceByOrderIdAsync(q.OrderId, ct);
}
