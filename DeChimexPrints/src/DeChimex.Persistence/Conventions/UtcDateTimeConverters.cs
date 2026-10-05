using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeChimex.Persistence.Conventions;

/// <summary>
/// Every DateTime the application persists is UTC (all properties are named *Utc and are
/// set from IDateTimeProvider.UtcNow). Relational providers return Kind=Unspecified on
/// read, so values are re-tagged as Utc. Writes are stored unchanged (no shifting).
/// Effect: JSON carries the "Z" suffix and ToLocalTime() converts correctly.
/// </summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v,
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

/// <inheritdoc cref="UtcDateTimeConverter"/>
public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v,
    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
