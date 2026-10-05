using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Immutable ledger entry recording a single stock movement. Child of the Inventory aggregate.
/// Every change to stock MUST produce one of these (auditable inventory history).
/// </summary>
public sealed class InventoryTransaction : Entity
{
    private InventoryTransaction() { } // EF

    private InventoryTransaction(Guid inventoryId, Guid productId, InventoryTransactionType type,
        int quantityChange, int resultingQuantity, string? reference, string? notes, string? performedBy)
    {
        InventoryId = inventoryId;
        ProductId = productId;
        Type = type;
        QuantityChange = quantityChange;
        ResultingQuantity = resultingQuantity;
        Reference = reference;
        Notes = notes;
        PerformedBy = performedBy;
        OccurredOnUtc = DateTime.UtcNow;
    }

    public Guid InventoryId { get; private set; }
    public Guid ProductId { get; private set; }
    public InventoryTransactionType Type { get; private set; }
    public int QuantityChange { get; private set; }
    public int ResultingQuantity { get; private set; }
    public string? Reference { get; private set; }
    public string? Notes { get; private set; }
    public string? PerformedBy { get; private set; }
    public DateTime OccurredOnUtc { get; private set; }

    internal static InventoryTransaction Create(Guid inventoryId, Guid productId, InventoryTransactionType type,
        int quantityChange, int resultingQuantity, string? reference, string? notes, string? performedBy)
        => new(inventoryId, productId, type, quantityChange, resultingQuantity, reference, notes, performedBy);
}
