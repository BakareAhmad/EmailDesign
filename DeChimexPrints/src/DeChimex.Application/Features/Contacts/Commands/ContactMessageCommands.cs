using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using MediatR;

namespace DeChimex.Application.Features.Contacts.Commands;

/// <summary>Persists a public contact-form submission so staff can read it in the portal.</summary>
public sealed record SaveContactMessageCommand(
    string Name, string Email, string? Phone, string? Service, string Details) : ICommand<Unit>;

public sealed class SaveContactMessageHandler(
    IContactMessageRepository messages, IUnitOfWork uow, INotificationService notifications) : ICommandHandler<SaveContactMessageCommand, Unit>
{
    public async Task<Unit> Handle(SaveContactMessageCommand command, CancellationToken ct)
    {
        var message = ContactMessage.Create(
            command.Name, command.Email, command.Phone, command.Service, command.Details);
        await messages.AddAsync(message, ct);
        await uow.SaveChangesAsync(ct);

        // Persist a staff-audience notification so permitted staff see the enquiry in
        // their notification feed (and bell badge / real-time push), not only in the
        // messages inbox. Scoped to messages.view so only authorised staff receive it.
        var snippet = message.Details.Length <= 160
            ? message.Details
            : message.Details[..157] + "...";
        await notifications.NotifyAsync(
            NotificationType.NewMessage,
            "New contact message",
            $"{message.Name} ({message.Email}) sent: {snippet}",
            link: $"/admin/messages/{message.Id}",
            requiredPermission: Permissions.Messages.View,
            ct: ct);

        return Unit.Value;
    }
}

/// <summary>Records a staff reply on a contact message and emails it to the enquirer.</summary>
public sealed record SendContactMessageReplyCommand(
    Guid MessageId, string Subject, string Body, Guid? ActorUserId, string? ActorName) : ICommand<ContactReplyResult>;

/// <summary>
/// Outcome of a staff reply. The reply is ALWAYS persisted on the thread; Emailed reports
/// whether the provider actually accepted the email, so the UI can never claim a delivery
/// that did not happen (root-cause fix for the "reply doesn't send email" report: Brevo
/// rejects unauthorised IPs with 401 and the old fire-and-forget queue swallowed it while
/// the page still said "Reply emailed").
/// </summary>
public sealed record ContactReplyResult(bool Emailed, string? Failure = null);

public sealed class SendContactMessageReplyHandler(
    IContactMessageRepository messages, IUnitOfWork uow, IEmailService emailService,
    IEmailTemplateRenderer emailRenderer)
    : ICommandHandler<SendContactMessageReplyCommand, ContactReplyResult>
{
    public async Task<ContactReplyResult> Handle(SendContactMessageReplyCommand command, CancellationToken ct)
    {
        var message = await messages.GetByIdAsync(command.MessageId, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Contact message", command.MessageId);

        // Persisted as part of the aggregate so the reply thread survives even if
        // the email dispatch later fails (delivery issues are logged, not lost).
        message.AddReply(command.Subject, command.Body, command.ActorUserId, command.ActorName);
        messages.Update(message);
        await uow.SaveChangesAsync(ct);

        var signature = string.IsNullOrWhiteSpace(command.ActorName)
            ? "DE-CHIMEX PRINT HUB"
            : $"{command.ActorName} - DE-CHIMEX PRINT HUB";
        var paragraphs = command.Body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => $"<p style='margin:0 0 12px'>{System.Net.WebUtility.HtmlEncode(p)}</p>");
        // Branded reply in the shared shell; paragraphs are encoded here (trusted Raw fragment).
        // No colours are set here: the shell's .dc-body normalisation owns the palette so this
        // greeting can never drift from the email design system.
        var rawBody =
            $"<p style='margin:0 0 14px'>Hello {System.Net.WebUtility.HtmlEncode(message.Name)},</p>" +
            string.Concat(paragraphs);
        var content = emailRenderer.Render(EmailTemplate.ContactReply, new Dictionary<string, string>
        {
            ["Subject"] = command.Subject.Trim(),
            ["RawBody"] = rawBody,
            ["Signature"] = signature
        });
        var plainBody = $"Hello {message.Name},\n\n{command.Body.Trim()}\n\n-- {signature}";

        // Sent synchronously (not via the fire-and-forget queue): the staff member needs an
        // honest, immediate answer about delivery. TrySendAsync never throws for provider
        // failures and logs the provider response for diagnosis.
        var emailed = await emailService.TrySendAsync(
            new EmailMessage(message.Email, message.Name, content.Subject, content.HtmlBody, plainBody, Template: nameof(EmailTemplate.ContactReply), Category: "contact"), ct);

        return emailed
            ? new ContactReplyResult(true)
            : new ContactReplyResult(false,
                "The email provider did not accept the message (or email sending is disabled). " +
                "The reply is saved on the thread. Check the Brevo authorised-IP settings and the app log for details.");
    }
}

/// <summary>Marks a contact message as resolved (or reopens it) for the handling staff member.</summary>
public sealed record ResolveContactMessageCommand(Guid Id, bool Resolve, Guid? ActorUserId) : ICommand<Unit>;

public sealed class ResolveContactMessageHandler(
    IContactMessageRepository messages, IUnitOfWork uow) : ICommandHandler<ResolveContactMessageCommand, Unit>
{
    public async Task<Unit> Handle(ResolveContactMessageCommand command, CancellationToken ct)
    {
        var message = await messages.GetByIdAsync(command.Id, ct)
            ?? throw new DeChimex.Application.Common.Exceptions.NotFoundException("Contact message", command.Id);

        if (command.Resolve)
            message.Resolve(command.ActorUserId);
        else
            message.Reopen();

        messages.Update(message);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
