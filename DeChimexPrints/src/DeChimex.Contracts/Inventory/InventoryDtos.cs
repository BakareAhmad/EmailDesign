namespace DeChimex.Contracts.Inventory;

public sealed record InventoryDto(Guid Id, Guid ProductId, string ProductName, int QuantityOnHand, int QuantityReserved, int Available, int LowStockThreshold, bool IsLowStock, bool IsOutOfStock);
public sealed record InventoryTransactionDto(Guid Id, string Type, int QuantityChange, int ResultingQuantity, string? Reference, string? Notes, string? PerformedBy, DateTime OccurredOnUtc);
public sealed record StockInRequest(int Quantity, string? Reference, string? Notes);
public sealed record StockAdjustmentRequest(int Delta, string Reason);
public sealed record SetThresholdRequest(int Threshold);
