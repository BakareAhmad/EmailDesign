using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable enable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessActivityAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded (see helpers at the bottom): 20260923120646_updatedMigration adds the same
            // objects, and on some databases it was applied first.
            AddColumnIfMissing(migrationBuilder, "ActivityType", "nvarchar(80) NULL");
            AddColumnIfMissing(migrationBuilder, "ActorName", "nvarchar(120) NULL");
            AddColumnIfMissing(migrationBuilder, "ActorRole", "nvarchar(32) NULL");
            AddColumnIfMissing(migrationBuilder, "ActorUserId", "uniqueidentifier NULL");
            AddColumnIfMissing(migrationBuilder, "Category", "nvarchar(32) NULL");
            AddColumnIfMissing(migrationBuilder, "CustomerId", "uniqueidentifier NULL");
            AddColumnIfMissing(migrationBuilder, "Importance", "nvarchar(16) NULL");
            AddColumnIfMissing(migrationBuilder, "IsRealtime", "bit NOT NULL DEFAULT CAST(0 AS bit)");
            AddColumnIfMissing(migrationBuilder, "OrderId", "uniqueidentifier NULL");
            AddColumnIfMissing(migrationBuilder, "ProductId", "uniqueidentifier NULL");
            AddColumnIfMissing(migrationBuilder, "RealtimeDispatchedAtUtc", "datetime2 NULL");
            AddColumnIfMissing(migrationBuilder, "SourceKey", "nvarchar(180) NULL");
            AddColumnIfMissing(migrationBuilder, "StaffUserId", "uniqueidentifier NULL");
            AddColumnIfMissing(migrationBuilder, "Summary", "nvarchar(800) NULL");
            AddColumnIfMissing(migrationBuilder, "Title", "nvarchar(160) NULL");

            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_ActorUserId_TimestampUtc", "[ActorUserId], [TimestampUtc]");
            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_Category_TimestampUtc_Id", "[Category], [TimestampUtc], [Id]");
            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_CustomerId_TimestampUtc", "[CustomerId], [TimestampUtc]");
            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_IsRealtime_RealtimeDispatchedAtUtc_TimestampUtc", "[IsRealtime], [RealtimeDispatchedAtUtc], [TimestampUtc]");
            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_OrderId_TimestampUtc", "[OrderId], [TimestampUtc]");
            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_ProductId_TimestampUtc", "[ProductId], [TimestampUtc]");
            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_SourceKey", "[SourceKey]", unique: true, filter: "[SourceKey] IS NOT NULL");
            CreateIndexIfMissing(migrationBuilder, "IX_AuditLogs_StaffUserId_TimestampUtc", "[StaffUserId], [TimestampUtc]");

            migrationBuilder.Sql(@"
UPDATE a SET
 OldValues = CASE WHEN EntityName IN ('Product','Category','MajorCategory','Inventory','Role','Review','Order','Payment') THEN
 (SELECT '{' + STRING_AGG(CONVERT(nvarchar(max), '""' + STRING_ESCAPE(j.[key], 'json') + '"":' + CASE WHEN j.[type]=1 THEN '""' + STRING_ESCAPE(j.[value], 'json') + '""' WHEN j.[type]=0 THEN 'null' ELSE j.[value] END), ',') + '}'
  FROM OPENJSON(CASE WHEN ISJSON(a.OldValues)=1 THEN a.OldValues ELSE '{}' END) j
  WHERE j.[type] IN (0,1,2,3) AND j.[key] IN ('Name','CategoryId','MajorCategoryId','IsActive','IsFeatured','IsCustomizable','DisplayOrder','Status','OrderId','CustomerId','ProductId','Rating','IsApproved','QuantityOnHand','QuantityReserved','LowStockThreshold')) ELSE NULL END,
 NewValues = CASE WHEN EntityName IN ('Product','Category','MajorCategory','Inventory','Role','Review','Order','Payment') THEN
 (SELECT '{' + STRING_AGG(CONVERT(nvarchar(max), '""' + STRING_ESCAPE(j.[key], 'json') + '"":' + CASE WHEN j.[type]=1 THEN '""' + STRING_ESCAPE(j.[value], 'json') + '""' WHEN j.[type]=0 THEN 'null' ELSE j.[value] END), ',') + '}'
  FROM OPENJSON(CASE WHEN ISJSON(a.NewValues)=1 THEN a.NewValues ELSE '{}' END) j
  WHERE j.[type] IN (0,1,2,3) AND j.[key] IN ('Name','CategoryId','MajorCategoryId','IsActive','IsFeatured','IsCustomizable','DisplayOrder','Status','OrderId','CustomerId','ProductId','Rating','IsApproved','QuantityOnHand','QuantityReserved','LowStockThreshold')) ELSE NULL END
FROM AuditLogs a WHERE ActivityType IS NULL;

IF NOT EXISTS (SELECT 1 FROM Permissions WHERE Name='activity.view')
 INSERT INTO Permissions (Id,Name,Description) VALUES (NEWID(),'activity.view','View business activity');
INSERT INTO RolePermissions (Id,RoleId,PermissionId)
 SELECT NEWID(),r.Id,p.Id FROM Roles r CROSS JOIN Permissions p
 WHERE r.NormalizedName='OWNER' AND r.IsDeleted=0 AND p.Name='activity.view'
 AND NOT EXISTS(SELECT 1 FROM RolePermissions rp WHERE rp.RoleId=r.Id AND rp.PermissionId=p.Id);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ActorUserId_TimestampUtc",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Category_TimestampUtc_Id",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_CustomerId_TimestampUtc",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_IsRealtime_RealtimeDispatchedAtUtc_TimestampUtc",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_OrderId_TimestampUtc",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ProductId_TimestampUtc",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_SourceKey",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_StaffUserId_TimestampUtc",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ActivityType",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorName",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorRole",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Importance",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "IsRealtime",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "RealtimeDispatchedAtUtc",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "StaffUserId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "AuditLogs");
        }

        // ---- idempotent helpers (SQL Server) -------------------------------------------------
        // These AuditLogs columns/indexes are declared by BOTH 20260922040450_AddBusinessActivityAudit
        // and 20260923120646_updatedMigration (the latter was scaffolded from a model snapshot that
        // did not yet contain the former). Whichever of the two runs second on a given database
        // must therefore skip objects that already exist, otherwise `update-database` fails with
        // "Column name 'ActivityType' in table 'AuditLogs' is specified more than once".
        private static void AddColumnIfMissing(MigrationBuilder mb, string column, string definition) =>
            mb.Sql($"IF COL_LENGTH(N'[AuditLogs]', N'{column}') IS NULL BEGIN ALTER TABLE [AuditLogs] ADD [{column}] {definition}; END");

        private static void CreateIndexIfMissing(MigrationBuilder mb, string name, string columns, bool unique = false, string? filter = null) =>
            mb.Sql($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'{name}' AND [object_id] = OBJECT_ID(N'[AuditLogs]')) " +
                   $"BEGIN CREATE {(unique ? "UNIQUE " : "")}INDEX [{name}] ON [AuditLogs] ({columns}){(filter is null ? "" : " WHERE " + filter)}; END");
    }
}
