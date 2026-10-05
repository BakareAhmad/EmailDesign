using DeChimex.Application.Common.Interfaces;

namespace DeChimex.Infrastructure.Common;

public sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
