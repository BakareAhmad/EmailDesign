using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.Orders;

namespace DeChimex.Application.Features.Orders;

/// <summary>
/// Workload-based sales assignment policy. Picks the eligible sales staff member with the
/// lowest active workload; ties break deterministically (earliest-created account, then
/// email) so repeated requests never produce unpredictable assignments.
/// </summary>
public static class SalesAssignmentPolicy
{
    public static SalesStaffOptionDto? Pick(IReadOnlyList<SalesStaffOptionDto> eligible)
        => eligible
            .OrderBy(s => s.ActiveWorkload)
            .ThenBy(s => s.CreatedDateUtc)
            .ThenBy(s => s.Email, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
}

/// <summary>Automatic (workload-based) sales assignment + eligibility checks for manual assignment.</summary>
public interface ISalesAssignmentService
{
    /// <summary>Picks the sales owner for a new request; null when no eligible staff exists.</summary>
    Task<SalesStaffOptionDto?> PickForNewOrderAsync(CancellationToken ct);

    /// <summary>Returns the display name when the user is eligible sales staff, else throws.</summary>
    Task<string> RequireEligibleSalesNameAsync(Guid userId, CancellationToken ct);
}

public sealed class SalesAssignmentService(ISalesInboxQueries sales) : ISalesAssignmentService
{
    public async Task<SalesStaffOptionDto?> PickForNewOrderAsync(CancellationToken ct)
        => SalesAssignmentPolicy.Pick(await sales.GetEligibleSalesStaffAsync(ct));

    public async Task<string> RequireEligibleSalesNameAsync(Guid userId, CancellationToken ct)
    {
        var match = (await sales.GetEligibleSalesStaffAsync(ct))
            .FirstOrDefault(s => s.UserId == userId);
        if (match is null)
            throw new ForbiddenException("That user is not eligible sales staff.");
        return match.Name;
    }
}
