using DeChimex.Domain.Common;

namespace DeChimex.Domain.Events;

/// <summary>Raised when a product's available stock falls at or below its threshold.</summary>
public sealed record LowStockDetectedEvent(Guid ProductId, string ProductName, int Available, int Threshold) : DomainEvent;

/// <summary>Raised when stock is requested but insufficient, triggering the procurement workflow.</summary>
public sealed record InventoryShortageDetectedEvent(Guid ProductId, string ProductName, int Requested, int Available) : DomainEvent;
