namespace DeChimex.Infrastructure.Options;

public sealed class BrevoOptions
{
    public const string SectionName = "Brevo";
    public string ApiKey { get; init; } = default!;
    public string ApiBaseUrl { get; init; } = "https://api.brevo.com/v3";
    public string SenderName { get; init; } = "DE-CHIMEX PRINT HUB";
    public string SenderEmail { get; init; } = "no-reply@dechimexprinthub.com";
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// DEV-ONLY: when set (and sending is disabled/unconfigured), each would-be email is
    /// also appended to this file so local/E2E automation can read verification tokens.
    /// Ignored entirely on the live Brevo path.
    /// </summary>
    public string DevOutboxPath { get; init; } = string.Empty;
}

public sealed class PaystackOptions
{
    public const string SectionName = "Paystack";
    public string SecretKey { get; init; } = default!;
    public string PublicKey { get; init; } = default!;
    public string ApiBaseUrl { get; init; } = "https://api.paystack.co";
    public string Currency { get; init; } = "NGN";

    /// <summary>
    /// Optional checkout channel restriction passed to Paystack as the `channels`
    /// initialize parameter (current docs: "card", "bank", "apple_pay", "ussd", "qr",
    /// "mobile_money", "bank_transfer", "eft", "capitec_pay", "payattitude"). Leave EMPTY
    /// (default) to omit the parameter — Paystack then shows every channel enabled on the
    /// integration/account, which is the current intended behaviour. When set, only the
    /// listed channels appear on Paystack's hosted checkout.
    /// </summary>
    public string[] Channels { get; init; } = Array.Empty<string>();

    /// <summary>When true (default) the app adds Paystack's published NGN charge on top of
    /// the order amount so the customer bears it (owner directive 2026-09-08). Set false
    /// ONLY if the "Pass fees to customers" toggle is enabled on the Paystack dashboard,
    /// which then adds the fee automatically at Paystack checkout.</summary>
    public bool CustomerBorneCharge { get; init; } = true;
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string Provider { get; init; } = "Local"; // Local | Cloudinary | AzureBlob
    public string LocalRootPath { get; init; } = "wwwroot/uploads";
    /// <summary>
    /// Physical root for CONFIDENTIAL categories (order attachments, chat media, design
    /// proofs/reviews) — deliberately OUTSIDE wwwroot (AUD-021): the files can only be
    /// reached through UploadAuthorizationMiddleware, never by static-file serving.
    /// Public storefront media (productimage, gallerymedia) stays under LocalRootPath.
    /// </summary>
    public string PrivateRootPath { get; init; } = "App_Data/private-uploads";
    public string PublicBaseUrl { get; init; } = "/uploads";
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    public string[] AllowedImageExtensions { get; init; } = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
    public string[] AllowedVideoExtensions { get; init; } = { ".mp4", ".webm" };
    public string[] AllowedAudioExtensions { get; init; } = { ".webm", ".mp3", ".ogg", ".oga", ".wav", ".m4a" };
    public string[] AllowedDocumentExtensions { get; init; } = { ".pdf", ".doc", ".docx" };
    
    /// <summary>
    /// Design review file extensions: images, documents, and design formats.
    /// These are preserved exactly without compression/resizing.
    /// </summary>
    public string[] AllowedDesignReviewExtensions { get; init; } = 
    {
        // Images
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".tif", ".tiff",
        // Documents
        ".pdf", ".svg",
        // Adobe formats
        ".psd", ".ai", ".eps",
        // Archives
        ".zip"
    };
}

public sealed class AppOptions
{
    public const string SectionName = "App";
    public string Name { get; init; } = "DE-CHIMEX PRINT HUB";
    public string FrontendBaseUrl { get; init; } = "https://dechimexprinthub.com";
    public string ApiBaseUrl { get; init; } = "https://api.dechimexprinthub.com";
    public string SupportEmail { get; init; } = "hello@dechimexprinthub.com";
    public string DefaultCurrency { get; init; } = "NGN";
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; init; } = Array.Empty<string>();
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";
    public string OwnerEmail { get; init; } = "owner@dechimexprinthub.com";
    public string OwnerPassword { get; init; } = "ChangeMe123!";
    public string OwnerFirstName { get; init; } = "System";
    public string OwnerLastName { get; init; } = "Owner";

    /// <summary>
    /// Developer Portal account. Seeded ONLY when both email and password are supplied
    /// (blank in appsettings.json; Development supplies sandbox values; other environments
    /// must use environment variables / secret storage: Seed__DeveloperEmail and
    /// Seed__DeveloperPassword). Never commit production credentials.
    /// </summary>
    public string? DeveloperEmail { get; init; }
    public string? DeveloperPassword { get; init; }
    public string DeveloperFirstName { get; init; } = "Platform";
    public string DeveloperLastName { get; init; } = "Developer";
}

/// <summary>
/// Marketing broadcast settings. AllowBroadcasts is the DEVELOPMENT SAFEGUARD: it is
/// false by default so no environment accidentally emails the entire customer list.
/// Enable it explicitly per environment (appsettings.Development.json sets it true for
/// local testing; production must set Marketing__AllowBroadcasts=true to arm sending).
/// </summary>
public sealed class MarketingOptions
{
    public const string SectionName = "Marketing";

    /// <summary>Master switch for actually dispatching broadcast emails.</summary>
    public bool AllowBroadcasts { get; init; } = false;

    /// <summary>Recipients processed per background batch.</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>Delay between background polls (seconds).</summary>
    public int PollIntervalSeconds { get; init; } = 10;
}
