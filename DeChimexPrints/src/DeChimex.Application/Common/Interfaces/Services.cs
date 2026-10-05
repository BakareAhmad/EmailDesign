using DeChimex.Domain.Enums;

namespace DeChimex.Application.Common.Interfaces;

/// <summary>Provides information about the currently authenticated principal.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    IReadOnlyList<string> Roles { get; }
    IReadOnlyList<string> Permissions { get; }
    string? IpAddress { get; }
    bool HasPermission(string permission);
}

/// <summary>Testable clock abstraction.</summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}

/// <summary>Hashes and verifies passwords using a strong algorithm.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);

    /// <summary>True when a stored hash was produced with weaker parameters than the
    /// current policy (AUD-025): callers re-hash on successful verification so legacy
    /// accounts upgrade transparently at next login.</summary>
    bool NeedsRehash(string hash);
}

/// <summary>Generates opaque secure tokens (email verification, password reset).</summary>
public interface ISecureTokenGenerator
{
    string GenerateToken();

    /// <summary>
    /// Generates a numeric one-time code of exactly <paramref name="digits"/> digits
    /// (zero-padded, cryptographically uniform - used for the TYPED email-verification
    /// token). Callers that deliver tokens via a CLICKED LINK should keep using
    /// <see cref="GenerateToken"/> (long opaque token) since typing burden does not
    /// apply there and the larger keyspace is free security.
    /// </summary>
    string GenerateNumericToken(int digits = 6);
}

/// <summary>Abstraction for sending transactional e-mail (implemented by Brevo).</summary>
public interface IEmailService
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>
    /// Sends and reports whether the provider accepted it. Used by the marketing
    /// broadcast worker for per-recipient delivery tracking. Never throws for
    /// provider failures.
    /// </summary>
    Task<bool> TrySendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Strips customer PII from gateway request/response payloads before they are persisted
/// in the financial ledger (AUD-033). Financial facts (amounts, references, statuses,
/// fees) are preserved; contact details and card-authorization metadata are redacted.
/// Non-JSON payloads are returned unchanged.
/// </summary>
public interface IGatewayPayloadRedactor
{
    string Redact(string? payload);
}

/// <param name="Template">Template name, recorded (without content) in Developer Portal email diagnostics.</param>
/// <param name="Category">Recipient category for diagnostics: customer | staff | contact | marketing | internal.</param>
public sealed record EmailMessage(
    string ToEmail,
    string ToName,
    string Subject,
    string HtmlBody,
    string? PlainTextBody = null,
    string? Template = null,
    string? Category = null);

/// <summary>Renders named e-mail templates with a model.</summary>
public interface IEmailTemplateRenderer
{
    EmailContent Render(EmailTemplate template, IReadOnlyDictionary<string, string> model);

    /// <summary>
    /// Renders a MARKETING email (admin-composed broadcast) in the brand shell.
    /// Deliberately a separate entry point from <see cref="Render"/>: marketing content
    /// is free-form sanitized HTML and must never be routed through the transactional
    /// template enum (and vice versa).
    /// </summary>
    EmailContent RenderMarketing(string subject, string htmlContent, IReadOnlyDictionary<string, string> model);
}

public sealed record EmailContent(string Subject, string HtmlBody, string PlainTextBody);

public enum EmailTemplate
{
    Welcome,
    EmailVerification,
    ForgotPassword,
    PasswordResetConfirmation,
    Otp,
    OrderConfirmation,
    OrderStatusUpdate,
    PaymentReceipt,
    PaymentFailed,

    // Custom-order workflow templates (CUSTOM_ORDER_FLOW_PLAN.md §11).
    OrderRequestReceived,
    OrderMessageNotification,
    OrderQuoteReady,
    DesignProofReady,
    DesignProofApproved,

    // Contact flows — these emails always existed (raw HTML); they now share the
    // brand shell with every other outgoing email (presentation only, same triggers).
    ContactMessageReceived,
    ContactReply,

    // Admin diagnostic ("Send test email to my address") — same shell, same trigger.
    ProviderTestEmail
}

/// <summary>Queue abstraction so email dispatch can be moved to a background worker later.</summary>
public interface IEmailQueue
{
    Task EnqueueAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Outcome of an email-provider connectivity check (admin diagnostics).</summary>
public sealed record EmailHealthResult(bool Healthy, string Status, string? Detail = null, string? FixUrl = null);

/// <summary>
/// Verifies the email provider is reachable and willing to accept sends FROM THIS SERVER.
/// Exists because provider-side rejections (e.g. Brevo's authorised-IP restriction) are
/// invisible to users otherwise: sends silently fail while the app looks healthy.
/// </summary>
public interface IEmailHealthCheck
{
    /// <summary>Checks provider connectivity; when <paramref name="testRecipient"/> is set,
    /// also sends a test email and reports whether the provider accepted it.</summary>
    Task<EmailHealthResult> CheckAsync(string? testRecipient, CancellationToken ct = default);
}

/// <summary>Payment gateway abstraction (implemented by Paystack).</summary>
public interface IPaymentGateway
{
    Task<PaymentInitializationResult> InitializeAsync(PaymentInitializationRequest request, CancellationToken ct = default);
    Task<PaymentVerificationResult> VerifyAsync(string reference, CancellationToken ct = default);
    bool VerifyWebhookSignature(string requestBody, string signatureHeader);
    Task<RefundResult> RefundAsync(string reference, decimal? amount, CancellationToken ct = default);
}

public sealed record PaymentInitializationRequest(string Reference, string Email, decimal Amount, string Currency, string CallbackUrl, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record PaymentInitializationResult(bool Success, string? AuthorizationUrl, string? GatewayReference, string? Message);

/// <summary>
/// Outcome of a gateway verification, following Paystack's documented transaction
/// status vocabulary (verify-payments): success | failed | abandoned | ongoing |
/// pending | processing | queued | reversed. In-flight/unknown statuses must NOT be
/// collapsed into failure: async channels (bank transfer, USSD, mobile money) sit in
/// ongoing/pending/processing while the customer completes the payment.
/// </summary>
public enum PaymentVerificationOutcome
{
    /// <summary>Provider confirmed the exact expected charge. Safe to mark paid.</summary>
    Succeeded,
    /// <summary>Provider reported a terminal unpaid outcome (failed / abandoned / reversed).</summary>
    Failed,
    /// <summary>
    /// Not settled yet: provider status is in-flight (pending / processing / queued /
    /// ongoing / unknown), or the verification request itself could not be completed.
    /// The payment must stay retryable and unconfirmed — the webhook can still settle it.
    /// </summary>
    Pending
}

public sealed record PaymentVerificationResult(
    PaymentVerificationOutcome Outcome,
    string? GatewayReference,
    decimal AmountPaid,
    string? Currency,
    string? RawResponse,
    string? Message,
    /// <summary>The authoritative transaction fee Paystack reported (verify response
    /// `fees`, converted to naira); null when the provider did not report one.</summary>
    decimal? Fees = null);

public sealed record RefundResult(bool Success, string? Message, string? RefundReference);

/// <summary>Secure file storage abstraction (local now, Cloudinary/Azure Blob later).</summary>
public interface IFileStorageService
{
    Task<FileUploadResult> UploadAsync(FileUploadRequest request, CancellationToken ct = default);
    Task DeleteAsync(string storedFileName, CancellationToken ct = default);
}

public sealed record FileUploadRequest(Stream Content, string OriginalFileName, string ContentType, long Length, FileCategory Category);
public sealed record FileUploadResult(string Url, string StoredFileName, long Size, string? PreviewUrl = null, string? PreviewStoredFileName = null);

public enum FileCategory { ProductImage, GalleryMedia, InvoiceDocument, Document, OrderAttachment, DesignProof, DesignReview, ChatMedia }

/// <summary>
/// Read-side authorization for confidential uploads (AUD-021): answers whether the current
/// user may view a private upload URL (order attachment, chat media, proof, review file).
/// Public storefront media (product images, gallery) never goes through this check.
/// </summary>
public interface IUploadAccessService
{
    Task<bool> CanViewAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Normalizes uploaded media so browsers can actually play/decode it.
/// Browsers only decode a subset of video codecs (H.264/AVC, VP8/VP9, AV1);
/// many camera/phone/editor exports (MPEG-4 Part 2, HEVC, etc.) will show a play
/// button but never advance. The storage layer calls this for video uploads and,
/// when a compatible encoder (ffmpeg) is available, re-encodes to H.264 + fast start.
/// Implementations must fail gracefully (return false / throw handled) so an upload
/// still succeeds even when no encoder is present.
/// </summary>
public interface IVideoEncodeService
{
    /// <summary>
    /// Rewrites <paramref name="sourceFile"/> in place so a browser can play it.
    /// Returns true when the file was (re)encoded or remuxed. Returns false when the
    /// file was left untouched (already browser-compatible, or no encoder available).
    /// </summary>
    Task<bool> MakeBrowserPlayableAsync(string sourceFile, CancellationToken ct = default);
}

/// <summary>Caching abstraction with a Redis-ready contract.</summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? absoluteExpiration = null, CancellationToken ct = default);
}


/// <summary>Raises in-app notifications and optional emails for system events.</summary>
public interface INotificationService
{
    /// <param name="requiredPermission">Permission a staff member needs to see the notification
    /// (e.g. "orders.view"); only applied to staff notifications. Null keeps legacy all-staff visibility.</param>
    Task NotifyAsync(NotificationType type, string title, string message, Guid? recipientUserId = null, string? link = null, string? requiredPermission = null, CancellationToken ct = default);
}

/// <summary>
/// PWA Web Push dispatch (PWA task 2026-10-04). Delivers a browser push notification to
/// every registered device of ONE user. Implementations must be best-effort and
/// non-blocking: push failures never affect the notification itself. Only DIRECT
/// recipient notifications are dispatched; staff-audience broadcasts are not.
/// </summary>
public interface IWebPushDispatcher
{
    Task DispatchAsync(Guid userId, string title, string message, string? url, CancellationToken ct = default);
}

/// <summary>
/// Full breakdown of a gateway payment quote: the order (business) value, the applicable
/// Paystack transaction fee for the processed charge, and the configured service fee —
/// the portion of Paystack's fee charged to the customer (ServiceFee = PaystackFee ×
/// ServiceFeePercent / 100, rounded up to the kobo at the payable).
/// </summary>
public sealed record PaymentChargeBreakdown(
    decimal OrderAmount,
    decimal PaystackFee,
    decimal ServiceFeePercent,
    decimal ServiceFeeAmount,
    decimal CustomerPayable);

/// <summary>
/// Computes the customer-borne Paystack transaction charge (owner directive 2026-09-08:
/// the business order value must never absorb the gateway fee; service-fee feature
/// 2026-09-23 makes the passed-on portion configurable). Implemented with Paystack's
/// published NGN pricing so the final payable amount nets the business the exact order
/// value after Paystack deducts its charge and the service fee.
/// </summary>
public interface IPaymentChargeCalculator
{
    /// <summary>The Paystack charge to add on top of the order amount (100% pass-through;
    /// retained for compatibility — service-fee aware callers use <see cref="Compute"/>.</summary>
    decimal ComputeCustomerBorneCharge(decimal orderAmount);

    /// <summary>orderAmount + ComputeCustomerBorneCharge(orderAmount).</summary>
    decimal ComputeCustomerPayable(decimal orderAmount);

    /// <summary>
    /// Server-authoritative quote for a gateway payment. The service fee is
    /// <paramref name="serviceFeePercent"/> percent (0–100) of Paystack's transaction fee
    /// for the amount actually charged (the fee includes the service fee itself, so the
    /// payable is solved as the closed-form fixed point — never iterated).
    /// </summary>
    PaymentChargeBreakdown Compute(decimal orderAmount, decimal serviceFeePercent);
}

/// <summary>
/// Push transport for the order-contextual chat (SignalR, implemented in the Host layer).
/// The message is persisted by the command handler and then broadcast to the order's
/// group; the push is best-effort and never blocks or rolls back the persisted message.
/// </summary>
public interface IOrderChatPusher
{
    /// <param name="orderId">Order whose group receives the message.</param>
    /// <param name="payload">Wire-format message (already projected by the caller).</param>
    Task PushMessageAsync(Guid orderId, object payload, CancellationToken ct = default);

    /// <summary>Pushes a workspace lifecycle event (file/proof/status/...) to the order's
    /// group. Best-effort: delivery failures are swallowed so a committed change never
    /// surfaces as an error to the actor.</summary>
    /// <param name="orderId">Order whose group receives the event.</param>
    /// <param name="eventName">Client-side event name (orderFile, orderProof, orderStatus, ...).</param>
    /// <param name="payload">Wire-format payload (already projected by the caller).</param>
    Task PushOrderEventAsync(Guid orderId, string eventName, object payload, CancellationToken ct = default);
}

/// <summary>
/// Who currently has an order's workspace open. Transient by design: connections are
/// tracked in memory (a connection is proof of an open workspace; anything else would
/// be a guess), so presence claims never outlive what the server actually observes.
/// Registered as a singleton; all members are thread-safe.
/// </summary>
public interface IOrderPresenceTracker
{
    /// <param name="role">"customer" or "staff".</param>
    void Join(Guid orderId, string connectionId, string role);
    /// <summary>Removes a connection everywhere; returns the orders it watched.</summary>
    IReadOnlyList<Guid> Leave(string connectionId);
    bool IsMember(Guid orderId, string connectionId);
    string? RoleOf(string connectionId);
    /// <summary>Whether the message recipient's side has the workspace open now —
    /// the send handler uses this for the honest delivered/not-delivered call.</summary>
    bool IsRecipientConnected(Guid orderId, string senderRole);
    OrderPresenceSnapshot GetSnapshot(Guid orderId);
}

/// <summary>Point-in-time presence for one order's two conversation sides.</summary>
public sealed record OrderPresenceSnapshot(
    bool CustomerOnline, bool StaffOnline,
    DateTime? CustomerLastSeenUtc, DateTime? StaffLastSeenUtc);
