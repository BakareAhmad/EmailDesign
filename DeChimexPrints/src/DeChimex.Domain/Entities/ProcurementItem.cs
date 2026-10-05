using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>A line on a procurement request (product + quantity to purchase).</summary>
public sealed class ProcurementItem : Entity
{
    private ProcurementItem() { } // EF
    private ProcurementItem(Guid procurementId, Guid productId, string productName, int quantity, Money? unitCost)
    {
        ProcurementId = procurementId;
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        UnitCost = unitCost;
    }

    public Guid ProcurementId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = default!;
    public int Quantity { get; private set; }
    public Money? UnitCost { get; private set; }
    public int QuantityReceived { get; private set; }

    internal static ProcurementItem Create(Guid procurementId, Guid productId, string productName, int quantity, decimal? unitCost)
    {
        if (quantity <= 0)
            throw new BusinessRuleValidationException("procurementitem.quantity", "Quantity must be positive.");
        return new ProcurementItem(procurementId, productId, productName, quantity, unitCost.HasValue ? Money.Of(unitCost.Value) : null);
    }

    internal void Receive(int qty) => QuantityReceived += qty;
}
