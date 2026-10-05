namespace DeChimex.Contracts.Dashboard;

public sealed record DashboardKpiDto(
    decimal RevenueThisMonth, int TotalOrders, int PendingOrders, int CompletedOrders,
    decimal InventoryValue, int LowStockCount, int AwaitingProcurementCount,
    string Currency);

public sealed record RevenuePointDto(string Label, decimal Value);
public sealed record CategorySalesDto(string Category, decimal Amount, decimal Percentage);
