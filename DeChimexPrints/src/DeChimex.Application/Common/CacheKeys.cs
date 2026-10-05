namespace DeChimex.Application.Common;

/// <summary>
/// MED-004: Centralised cache-key definitions for the in-memory (Redis-ready) cache. Keeping the
/// keys here guarantees the read side (queries that populate the cache) and the write side
/// (commands that invalidate it) always agree on the exact key format.
/// </summary>
public static class CacheKeys
{
    public static string CategoriesAll(bool activeOnly) => $"categories:all:{activeOnly}";
    public static string ServicesAll(bool activeOnly) => $"services:all:{activeOnly}";
    public const string SettingsAll = "settings:all";

    /// <summary>Every cached category-list key (both active-only variants), used for invalidation.</summary>
    public static IReadOnlyList<string> CategoryLists { get; } = new[] { CategoriesAll(true), CategoriesAll(false) };

    /// <summary>Every cached service-list key (both active-only variants), used for invalidation.</summary>
    public static IReadOnlyList<string> ServiceLists { get; } = new[] { ServicesAll(true), ServicesAll(false) };
}
