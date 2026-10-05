namespace DeChimex.Domain.Common;

/// <summary>
/// Base class for aggregate roots. Aggregates are the only entities exposed through
/// repositories and are the transactional consistency boundary. They collect domain
/// events raised during behaviour, to be dispatched after commit.
/// </summary>
public abstract class AggregateRoot : Entity, IAuditable, ISoftDeletable
{
    private readonly List<IDomainEvent> _domainEvents = new();

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    // Audit fields
    public DateTime CreatedDateUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDateUtc { get; set; }
    public string? ModifiedBy { get; set; }

    // Soft delete
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedDateUtc { get; private set; }
    public string? DeletedBy { get; private set; }

    public void MarkDeleted(string? deletedBy)
    {
        IsDeleted = true;
        DeletedDateUtc = DateTime.UtcNow;
        DeletedBy = deletedBy;
    }

}
