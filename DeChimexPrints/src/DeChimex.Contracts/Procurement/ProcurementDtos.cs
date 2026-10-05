namespace DeChimex.Contracts.Procurement;

public sealed record ProcurementItemDto(Guid Id, Guid ProductId, string ProductName, int Quantity, decimal? UnitCost, int QuantityReceived);
public sealed record ProcurementDto(Guid Id, string Reference, Guid? SourceOrderId, string Status, string? ApprovedBy, DateTime? ApprovedAtUtc, DateTime? ReceivedAtUtc, string? SupplierName, string? Notes, IReadOnlyList<ProcurementItemDto> Items, string? SourceOrderNumber = null);
public sealed record CreateProcurementItemRequest(Guid ProductId, int Quantity, decimal? UnitCost);
public sealed record CreateProcurementRequest(Guid? SourceOrderId, IReadOnlyList<CreateProcurementItemRequest> Items, string? Notes);
public sealed record MarkPurchasedRequest(string? SupplierName);
