namespace DeChimex.Host.Models;

// Shared empty-state descriptor rendered by Views/Shared/_EmptyState.cshtml.
public sealed record EmptyState(
    string Icon,
    string Title,
    string? Subtitle = null,
    string? CtaHref = null,
    string? CtaText = null,
    bool Muted = false,
    bool Compact = false);

// SVG path data for the app icon set (stroke based, 24x24 viewBox).
public static class EmptyIcons
{
    public const string Box = "M21 8v8a2 2 0 0 1-1 1.7l-7 4a2 2 0 0 1-2 0l-7-4A2 2 0 0 1 3 16V8a2 2 0 0 1 1-1.7l7-4a2 2 0 0 1 2 0l7 4A2 2 0 0 1 21 8z|M3.3 7 12 12l8.7-5|M12 22V12";
    public const string Cart = "M9 22a1 1 0 1 0 0-2 1 1 0 0 0 0 2zM20 22a1 1 0 1 0 0-2 1 1 0 0 0 0 2zM1 1h4l2.7 13.4a2 2 0 0 0 2 1.6h9.7a2 2 0 0 0 2-1.6L23 6H6";
    public const string Folder = "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z";
    public const string Users = "M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2|M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8|M23 21v-2a4 4 0 0 0-3-3.9|M16 3.1a4 4 0 0 1 0 7.8";
    public const string User = "M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2|M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8";
    public const string Receipt = "M4 2h16v20l-3-2-2 2-2-2-2 2-2-2-3 2zM8 7h8M8 11h8M8 15h5";
    public const string FileText = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6|M9 13h6M9 17h4";
    public const string Truck = "M1 3h15v13H1zM16 8h4l3 3v5h-7|M5.5 21a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5zM18.5 21a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z";
    public const string Star = "M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z";
    public const string Image = "M3 3h18v18H3zM8.5 10a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3M21 15l-5-5L5 21";
    public const string Heart = "M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.6l-1-1a5.5 5.5 0 1 0-7.8 7.8l1 1L12 21l7.8-7.6 1-1a5.5 5.5 0 0 0 0-7.8z";
    public const string Card = "M2 5h20v14H2zM2 10h20";
    public const string Chart = "M3 3v18h18|M18 17V9M13 17V5M8 17v-3";
    public const string Cash = "M20 12V8H6a2 2 0 0 1 0-4h12v4|M4 6v12a2 2 0 0 0 2 2h14v-4|M18 12a2 2 0 0 0 0 4h4v-4z";
    public const string Search = "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z|M21 21l-4.35-4.35";
    public const string Inbox = "M22 12h-6l-2 3h-4l-2-3H2|M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z";
    public const string Check = "M22 11.08V12a10 10 0 1 1-5.93-9.14|M22 4 12 14.01l-3-3";
    public const string Lock = "M5 11h14v10H5z|M7 11V7a5 5 0 0 1 10 0v4";
    public const string Wrench = "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z";
}
