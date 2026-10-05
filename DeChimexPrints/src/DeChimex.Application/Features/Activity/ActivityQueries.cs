using System.ComponentModel.DataAnnotations;
namespace DeChimex.Application.Features.Activity;
public sealed class ActivityFilter
{
    [StringLength(32)] public string? Category { get; set; }
    [StringLength(80)] public string? Type { get; set; }
    [StringLength(120)] public string? Search { get; set; }
    [StringLength(120)] public string? Actor { get; set; }
    [StringLength(64)] public string? Order { get; set; }
    [StringLength(120)] public string? Customer { get; set; }
    [StringLength(120)] public string? Product { get; set; }
    [StringLength(120)] public string? Staff { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? StaffUserId { get; set; }
    public Guid? ActorUserId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    [RegularExpression("^(normal|attention)$")] public string? Importance { get; set; }
    [Range(1,100000)] public int Page { get; set; } = 1;
    // On background updates, historical-only entries wait until explicit refresh/reconnect.
    public DateTime? HistoryCutoff { get; set; }
}
public sealed record ActivityItem(Guid Id,string Type,string Category,string Title,string Summary,DateTime TimestampUtc,
    Guid? ActorUserId,string ActorName,string ActorRole,string EntityName,string? EntityId,
    Guid? OrderId,Guid? CustomerId,Guid? ProductId,Guid? StaffUserId,string Importance,bool IsRealtime);
public sealed record ActivityPage(IReadOnlyList<ActivityItem> Items,int Page,bool HasNext) { public DateTime AsOfUtc {get;init;} = DateTime.UtcNow; }
public interface IActivityQueries { Task<ActivityPage> SearchAsync(ActivityFilter filter,CancellationToken ct); }
