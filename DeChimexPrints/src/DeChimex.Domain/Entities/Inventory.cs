using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Tracks stock for a single finished product. Aggregate root owning its transaction ledger.
///
/// Business rules enforced here:
///  - Every quantity change writes an <see cref="InventoryTransaction"/>.
///  - Orders are NEVER blocked by insufficient stock; instead a shortage event is raised
///    so the procurement workflow can be triggered (stock may go into backorder via reservation).
///  - Available = OnHand minus Reserved.
///  - Low-stock events fire when Available drops to/under the threshold.
/// </summary>
public sealed class Inventory : AggregateRoot
{
    private readonly List<InventoryTransaction> _transactions = new();

    private Inventory() { } // EF

    private Inventory(Guid productId, string productName, int initialQuantity, int lowStockThreshold)
    {
        ProductId = productId;
        ProductName = productName;
        QuantityOnHand = initialQuantity;
        QuantityReserved = 0;
        LowStockThreshold = lowStockThreshold;
    }

    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = default!;
    public int QuantityOnHand { get; private set; }
    public int QuantityReserved { get; private set; }
    public int LowStockThreshold { get; private set; }

    public int Available => QuantityOnHand - QuantityReserved;
    public bool IsLowStock => Available <= LowStockThreshold;
    public bool IsOutOfStock => Available <= 0;

    public IReadOnlyCollection<InventoryTransaction> Transactions => _transactions.AsReadOnly();

    public static Inventory Create(Guid productId, string productName, int initialQuantity = 0, int lowStockThreshold = 10)
    {
        if (productId == Guid.Empty)
            throw new BusinessRuleValidationException("inventory.product", "A valid product is required.");
        if (initialQuantity < 0)
            throw new BusinessRuleValidationException("inventory.quantity", "Initial quantity cannot be negative.");
        if (lowStockThreshold < 0)
            throw new BusinessRuleValidationException("inventory.threshold", "Threshold cannot be negative.");

        var inventory = new Inventory(productId, productName, initialQuantity, lowStockThreshold);
        if (initialQuantity > 0)
            inventory.RecordTransaction(InventoryTransactionType.StockIn, initialQuantity, "initial", "Opening stock", null);
        return inventory;
    }

    public void SetThreshold(int threshold)
    {
        if (threshold < 0)
            throw new BusinessRuleValidationException("inventory.threshold", "Threshold cannot be negative.");
        LowStockThreshold = threshold;
    }

    /// <summary>Receive stock (e.g. from procurement or restock).</summary>
    public void StockIn(int quantity, string? reference, string? notes, string? performedBy)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("inventory.stockin", "Stock-in quantity must be positive.");
        QuantityOnHand += quantity;
        RecordTransaction(InventoryTransactionType.StockIn, quantity, reference, notes, performedBy);
    }

    /// <summary>
    /// Deduct stock (fulfilment after payment). Deducts on-hand and releases any reservation.
    /// </summary>
    public void StockOut(int quantity, string? reference, string? notes, string? performedBy)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("inventory.stockout", "Stock-out quantity must be positive.");

        QuantityOnHand -= quantity;
        if (QuantityReserved >= quantity) QuantityReserved -= quantity;
        else QuantityReserved = 0;

        RecordTransaction(InventoryTransactionType.StockOut, -quantity, reference, notes, performedBy);
        EvaluateLowStock();
    }

    /// <summary>Manual adjustment (stock count correction). Delta may be positive or negative.</summary>
    public void Adjust(int delta, string? reason, string? performedBy)
    {
        if (delta == 0)
            throw new BusinessRuleValidationException("inventory.adjust", "Adjustment delta cannot be zero.");
        if (QuantityOnHand + delta < 0)
            throw new BusinessRuleValidationException("inventory.adjust.negative", "Adjustment would make stock negative.");

        QuantityOnHand += delta;
        RecordTransaction(InventoryTransactionType.Adjustment, delta, "adjustment", reason, performedBy);
        EvaluateLowStock();
    }

    /// <summary>
    /// Reserve stock for a paid order. Never blocks: if stock is short, the reservation still
    /// records what is available and raises a shortage event so procurement can top up.
    /// Returns the outstanding (backorder) quantity that must be procured.
    /// </summary>
    public int Reserve(int quantity, string? reference, string? performedBy)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("inventory.reserve", "Reservation quantity must be positive.");

        var reservable = Math.Min(quantity, Math.Max(0, Available));
        var shortage = quantity - reservable;

        if (reservable > 0)
        {
            QuantityReserved += reservable;
            RecordTransaction(InventoryTransactionType.Reservation, -reservable, reference, "Reserved for order", performedBy);
        }

        if (shortage > 0)
            RaiseDomainEvent(new InventoryShortageDetectedEvent(ProductId, ProductName, quantity, Available));

        EvaluateLowStock();
        return shortage;
    }

    /// <summary>
    /// Fulfilment-time shortage (AUD-014): production stock-out could deduct less than the
    /// reserved quantity because stock moved on since payment. Raises the same shortage
    /// event as <see cref="Reserve"/> so procurement re-triggers; called by the fulfilment
    /// handler after the (capped) deduction.
    /// </summary>
    public void RaiseShortage(int shortfall, string? reference)
    {
        if (shortfall <= 0) return;
        RaiseDomainEvent(new InventoryShortageDetectedEvent(ProductId, ProductName, shortfall, Available));
    }

    private void RecordTransaction(InventoryTransactionType type, int change, string? reference, string? notes, string? performedBy)
        => _transactions.Add(InventoryTransaction.Create(Id, ProductId, type, change,
            type == InventoryTransactionType.Reservation || type == InventoryTransactionType.ReleaseReservation
                ? Available
                : QuantityOnHand,
            reference, notes, performedBy));

    private void EvaluateLowStock()
    {
        if (IsLowStock)
            RaiseDomainEvent(new LowStockDetectedEvent(ProductId, ProductName, Available, LowStockThreshold));
    }
}
