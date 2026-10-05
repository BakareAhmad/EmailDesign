using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable enable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class updatedMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DUPLICATE of 20260922040450_AddBusinessActivityAudit (same columns and indexes, no
            // backfill). Kept so databases that already recorded this migration stay consistent;
            // every statement is guarded so it is a no-op when the objects already exist.
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the AuditLogs activity columns/indexes are owned by
            // 20260922040450_AddBusinessActivityAudit, whose Down() removes them.
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
