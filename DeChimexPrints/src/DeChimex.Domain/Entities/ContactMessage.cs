using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A contact-form submission from the public site. Persisted so staff can read it in
/// the admin portal even when no email provider is configured. Optionally resolved
/// once the enquiry has been handled.
/// </summary>
public sealed class ContactMessage : AggregateRoot
{
    private ContactMessage() { } // EF

    private ContactMessage(string name, string email, string? phone, string? service, string details)
    {
        Name = name;
        Email = email;
        Phone = phone;
        Service = service;
        Details = details;
        CreatedUtc = DateTime.UtcNow;
        IsResolved = false;
    }

    public string Name { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string? Phone { get; private set; }
    public string? Service { get; private set; }
    public string Details { get; private set; } = default!;
    public DateTime CreatedUtc { get; private set; }
    public bool IsResolved { get; private set; }
    public DateTime? ResolvedAtUtc { get; private set; }
    public Guid? ResolvedByUserId { get; private set; }

    private readonly List<ContactMessageReply> _replies = [];
    /// <summary>Staff email replies sent for this enquiry, oldest first.</summary>
    public IReadOnlyCollection<ContactMessageReply> Replies => _replies.AsReadOnly();

    /// <summary>Records a staff email reply as part of this aggregate.</summary>
    public ContactMessageReply AddReply(string subject, string body, Guid? sentByUserId, string? sentByName)
    {
        var reply = ContactMessageReply.Create(Id, subject, body, sentByUserId, sentByName);
        _replies.Add(reply);
        return reply;
    }

    public static ContactMessage Create(string name, string email, string? phone, string? service, string details)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("contact.name", "Name is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw new BusinessRuleValidationException("contact.email", "Email is required.");
        if (string.IsNullOrWhiteSpace(details))
            throw new BusinessRuleValidationException("contact.details", "Message is required.");

        return new(name.Trim(), email.Trim(),
            string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            string.IsNullOrWhiteSpace(service) ? null : service.Trim(),
            details.Trim());
    }

    public void Resolve(Guid? resolvedByUserId)
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedAtUtc = DateTime.UtcNow;
        ResolvedByUserId = resolvedByUserId;
    }

    public void Reopen()
    {
        if (!IsResolved) return;
        IsResolved = false;
        ResolvedAtUtc = null;
        ResolvedByUserId = null;
    }
}
