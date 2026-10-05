using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;
using DeChimex.Domain.ValueObjects;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Customer aggregate. Optionally linked to a login <see cref="User"/> account.
/// Owns saved addresses and tracks lifetime spending.
/// </summary>
public sealed class Customer : AggregateRoot
{
    private readonly List<CustomerAddress> _addresses = new();

    private Customer() { } // EF

    private Customer(string firstName, string lastName, Email? email, PhoneNumber? phone, Guid? userId, string? notes, bool isGuest)
    {
        FirstName = firstName;
        LastName = lastName;
        Email = email;
        Phone = phone;
        UserId = userId;
        Notes = notes?.Trim();
        LifetimeSpending = Money.Zero();
        IsGuest = isGuest;
    }

    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;
    public Email? Email { get; private set; }
    public PhoneNumber? Phone { get; private set; }
    public string? Notes { get; private set; }
    public Money LifetimeSpending { get; private set; } = default!;
    public Guid? UserId { get; private set; }

    /// <summary>
    /// True when the customer was created at the counter without an email address
    /// (walk-in guest). Guests have no portal login; adding an email later is how
    /// they get linked to an account.
    /// </summary>
    public bool IsGuest { get; private set; }

    /// <summary>
    /// Marketing consent. Registered customers default to subscribed (they signed up
    /// with the business); unsubscribing via the link in any marketing email opts them
    /// out permanently until they explicitly re-subscribe. Transactional emails
    /// (orders, receipts, account security) are NOT marketing and ignore this flag.
    /// </summary>
    public bool MarketingSubscribed { get; private set; } = true;

    /// <summary>Opaque per-customer token used in marketing unsubscribe links.</summary>
    public Guid UnsubscribeToken { get; private set; } = Guid.NewGuid();

    public string FullName => $"{FirstName} {LastName}".Trim();
    public IReadOnlyCollection<CustomerAddress> Addresses => _addresses.AsReadOnly();

    /// <summary>Opts the customer out of marketing broadcasts (idempotent).</summary>
    public void UnsubscribeFromMarketing() => MarketingSubscribed = false;

    /// <summary>Explicit re-subscribe (only via the customer's own action).</summary>
    public void ResubscribeToMarketing() => MarketingSubscribed = true;

    public static Customer Create(string firstName, string lastName, string? email = null, string? phone = null, Guid? userId = null, string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            throw new BusinessRuleValidationException("customer.firstname", "First name is required.");
        if (string.IsNullOrWhiteSpace(lastName))
            throw new BusinessRuleValidationException("customer.lastname", "Last name is required.");

        // A customer without an email is a walk-in guest (no portal login).
        var emailVo = string.IsNullOrWhiteSpace(email) ? null : Email.Create(email);
        var phoneVo = string.IsNullOrWhiteSpace(phone) ? null : PhoneNumber.Create(phone);
        return new Customer(firstName.Trim(), lastName.Trim(), emailVo, phoneVo, userId, notes, isGuest: emailVo is null);
    }

    public void UpdateProfile(string firstName, string lastName, string? phone, string? notes)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            throw new BusinessRuleValidationException("customer.firstname", "First name is required.");
        if (string.IsNullOrWhiteSpace(lastName))
            throw new BusinessRuleValidationException("customer.lastname", "Last name is required.");
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : PhoneNumber.Create(phone);
        Notes = notes?.Trim();
    }


    public void RecordSpending(Money amount) => LifetimeSpending = LifetimeSpending.Add(amount);

    /// <summary>
    /// AUD-034 reconciliation: replaces the denormalised value with one recomputed from
    /// order/payment history (orders with a successful payment, excluding refunded).
    /// Invoked only by the Dev-portal maintenance action; normal writes keep using
    /// RecordSpending/ReverseSpending so the paired invariant stays intact.
    /// </summary>
    public void RecomputeSpending(Money recomputedNetTotal) => LifetimeSpending = recomputedNetTotal;

    /// <summary>
    /// Reverses previously recorded lifetime spending when a paid order is refunded
    /// (business rule: lifetime spending is NET of refunds). Never goes below zero —
    /// defensive for rows recorded before spending was tracked.
    /// </summary>
    public void ReverseSpending(Money amount)
    {
        var reversed = LifetimeSpending.Amount - amount.Amount;
        LifetimeSpending = Money.Of(reversed < 0m ? 0m : reversed, LifetimeSpending.Currency);
    }

    public CustomerAddress AddAddress(string label, Address address, bool isDefault = false)
    {
        var makeDefault = isDefault || _addresses.Count == 0;
        if (makeDefault)
            foreach (var a in _addresses) a.SetDefault(false);

        var entity = CustomerAddress.Create(Id, label, address, makeDefault);
        _addresses.Add(entity);
        return entity;
    }

    public void RemoveAddress(Guid addressId)
    {
        var addr = _addresses.FirstOrDefault(a => a.Id == addressId);
        if (addr is null) return;
        _addresses.Remove(addr);
        if (addr.IsDefault && _addresses.Count > 0)
            _addresses[0].SetDefault(true);
    }
}
