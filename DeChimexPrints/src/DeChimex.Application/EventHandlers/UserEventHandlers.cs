using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DeChimex.Application.EventHandlers;

public sealed class UserRegisteredEventHandler(
    IEmailQueue emailQueue,
    IEmailTemplateRenderer renderer,
    IAppUrlProvider urls,
    ILogger<UserRegisteredEventHandler> logger) : INotificationHandler<UserRegisteredEvent>
{
    public async Task Handle(UserRegisteredEvent e, CancellationToken ct)
    {
        var link = urls.BuildEmailVerificationLink(e.Email, e.EmailVerificationToken);
        var content = renderer.Render(EmailTemplate.EmailVerification, new Dictionary<string, string>
        {
            ["FirstName"] = e.FirstName,
            ["Token"] = e.EmailVerificationToken,
            ["VerificationLink"] = link
        });
        await emailQueue.EnqueueAsync(new EmailMessage(e.Email, e.FirstName, content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.EmailVerification), Category: "customer"), ct);
        logger.LogInformation("Queued verification email for {Email}", e.Email);
    }
}

public sealed class VerificationEmailRequestedEventHandler(
    IEmailQueue emailQueue,
    IEmailTemplateRenderer renderer,
    IAppUrlProvider urls,
    ILogger<VerificationEmailRequestedEventHandler> logger) : INotificationHandler<VerificationEmailRequestedEvent>
{
    public async Task Handle(VerificationEmailRequestedEvent e, CancellationToken ct)
    {
        var link = urls.BuildEmailVerificationLink(e.Email, e.EmailVerificationToken);
        var content = renderer.Render(EmailTemplate.EmailVerification, new Dictionary<string, string>
        {
            ["FirstName"] = e.FirstName,
            ["Token"] = e.EmailVerificationToken,
            ["VerificationLink"] = link
        });
        await emailQueue.EnqueueAsync(new EmailMessage(e.Email, e.FirstName, content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.EmailVerification), Category: "customer"), ct);
        logger.LogInformation("Queued resent verification email for {Email}", e.Email);
    }
}

public sealed class UserEmailVerifiedEventHandler(
    IEmailQueue emailQueue, IEmailTemplateRenderer renderer) : INotificationHandler<UserEmailVerifiedEvent>
{
    public async Task Handle(UserEmailVerifiedEvent e, CancellationToken ct)
    {
        var content = renderer.Render(EmailTemplate.Welcome, new Dictionary<string, string> { ["FirstName"] = e.FirstName });
        await emailQueue.EnqueueAsync(new EmailMessage(e.Email, e.FirstName, content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.Welcome), Category: "customer"), ct);
    }
}

public sealed class PasswordResetRequestedEventHandler(
    IEmailQueue emailQueue, IEmailTemplateRenderer renderer, IAppUrlProvider urls) : INotificationHandler<PasswordResetRequestedEvent>
{
    public async Task Handle(PasswordResetRequestedEvent e, CancellationToken ct)
    {
        var link = urls.BuildPasswordResetLink(e.Email, e.ResetToken);
        var content = renderer.Render(EmailTemplate.ForgotPassword, new Dictionary<string, string>
        {
            ["FirstName"] = e.FirstName,
            ["ResetLink"] = link
        });
        await emailQueue.EnqueueAsync(new EmailMessage(e.Email, e.FirstName, content.Subject, content.HtmlBody, content.PlainTextBody, Template: nameof(EmailTemplate.ForgotPassword), Category: "customer"), ct);
    }
}
