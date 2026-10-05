using DeChimex.Domain.Common;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Procurement request to top up stock for backordered products. Follows the workflow:
/// Requested → Approved → Purchased → Received. Receiving increases inventory
/// (handled by the application layer via the Inventory aggregate).
/// </summary>
public sealed class Procurement : AggregateRoot
{
    private readonly List<ProcurementItem> _items = new();

    private Procurement() { } // EF

    private Procurement(string reference, Guid? sourceOrderId)
    {
        Reference = reference;
        SourceOrderId = sourceOrderId;
        Status = ProcurementStatus.Requested;
    }

    public string Reference { get; private set; } = default!;
    /// <summary>Persistence-only escape hatch (AUD-004): when the unique reference index
    /// rejects a count-generated number under concurrency, the save boundary renumbers the
    /// conflicting document with a fresh random suffix and retries. Never call during normal
    /// business flow.</summary>
    public void RenumberAfterReferenceConflict(string newNumber) => Reference = newNumber;

    public Guid? SourceOrderId { get; private set; }
    public ProcurementStatus Status { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public DateTime? ReceivedAtUtc { get; private set; }
    public string? SupplierName { get; private set; }
    public string? Notes { get; private set; }

    public IReadOnlyCollection<ProcurementItem> Items => _items.AsReadOnly();

    public static Procurement Request(string reference, Guid? sourceOrderId = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new BusinessRuleValidationException("procurement.reference", "Reference is required.");
        return new Procurement(reference, sourceOrderId);
    }

    public ProcurementItem AddItem(Guid productId, string productName, int quantity, decimal? unitCost = null)
    {
        if (Status != ProcurementStatus.Requested)
            throw new BusinessRuleValidationException("procurement.locked", "Items can only be added while requested.");
        var item = ProcurementItem.Create(Id, productId, productName, quantity, unitCost);
        _items.Add(item);
        return item;
    }

    public void Approve(string approvedBy)
    {
        if (Status != ProcurementStatus.Requested)
            throw new BusinessRuleValidationException("procurement.approve", "Only a requested procurement can be approved.");
        if (_items.Count == 0)
            throw new BusinessRuleValidationException("procurement.empty", "Cannot approve an empty procurement.");
        Status = ProcurementStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAtUtc = DateTime.UtcNow;
    }

    public void MarkPurchased(string? supplierName)
    {
        if (Status != ProcurementStatus.Approved)
            throw new BusinessRuleValidationException("procurement.purchase", "Only an approved procurement can be purchased.");
        Status = ProcurementStatus.Purchased;
        SupplierName = supplierName;
    }

    public void MarkReceived()
    {
        if (Status != ProcurementStatus.Purchased)
            throw new BusinessRuleValidationException("procurement.receive", "Only a purchased procurement can be received.");
        foreach (var item in _items) item.Receive(item.Quantity);
        Status = ProcurementStatus.Received;
        ReceivedAtUtc = DateTime.UtcNow;
    }

    public void Cancel()
    {
        if (Status == ProcurementStatus.Received)
            throw new BusinessRuleValidationException("procurement.cancel", "A received procurement cannot be cancelled.");
        Status = ProcurementStatus.Cancelled;
    }
}
