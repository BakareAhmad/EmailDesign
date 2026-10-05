using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>Key/value business setting (e.g. business name, default delivery fee, VAT rate).</summary>
public sealed class ApplicationSetting : AggregateRoot
{
    private ApplicationSetting() { } // EF
    private ApplicationSetting(string key, string value, string? description)
    {
        Key = key;
        Value = value;
        Description = description;
    }

    public string Key { get; private set; } = default!;
    public string Value { get; private set; } = default!;
    public string? Description { get; private set; }

    public static ApplicationSetting Create(string key, string value, string? description = null)
        => new(key, value, description);

    public void SetValue(string value) => Value = value;
}
