namespace DeChimex.Contracts.Orders;

// -----------------------------------------------------------------------------------
// Sales-assignment workflow read models (per-order ownership + sales inbox).
// -----------------------------------------------------------------------------------

/// <summary>An eligible sales staff member (active user holding the sales role).</summary>
public sealed record SalesStaffOptionDto(
    Guid UserId, string Name, string Email, int ActiveWorkload, DateTime CreatedDateUtc);

/// <summary>One row in the sales inbox/work queue.</summary>
public sealed record SalesInboxItemDto(
    Guid OrderId, string OrderNumber, string CustomerName, string Status, string ConversationState,
    string? AssignedSalesName, DateTime CreatedDateUtc, int MessageCount,
    DateTime? LastMessageAtUtc, string? LastMessagePreview);

/// <summary>Inbox bucket counts for the tab bar (scoped to the same staff filter as the list).</summary>
public sealed record SalesBucketCounts(
    int New, int Active, int WaitingForCustomer, int WaitingForStaff, int Resolved, int Unassigned);
