-- DE-CHIMEX — recovery helper for "Column name 'ActivityType' in table 'AuditLogs' is specified more than once"
-- ---------------------------------------------------------------------------------------------------------
-- Cause: migrations 20260922040450_AddBusinessActivityAudit and 20260923120646_updatedMigration both add the
-- same 15 AuditLogs columns + 8 indexes. On a database where one of them (or the idempotent
-- 20260922-BusinessActivity-Upgrade.sql) already ran, applying the other one failed.
--
-- Fix shipped in code (2026-09-25): both migrations are now guarded (IF COL_LENGTH(...) IS NULL / IF NOT EXISTS
-- index) so `update-database` simply completes. Pull the fixed source, rebuild, run `update-database` again.
--
-- This script is ONLY for people who cannot rebuild yet and want to unblock the database by hand. It is
-- idempotent: it adds whatever is missing and records both migrations as applied. Run it against DeChimexDb.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'[AuditLogs]', N'ActivityType') IS NULL            ALTER TABLE [AuditLogs] ADD [ActivityType] nvarchar(80) NULL;
IF COL_LENGTH(N'[AuditLogs]', N'ActorName') IS NULL               ALTER TABLE [AuditLogs] ADD [ActorName] nvarchar(120) NULL;
IF COL_LENGTH(N'[AuditLogs]', N'ActorRole') IS NULL               ALTER TABLE [AuditLogs] ADD [ActorRole] nvarchar(32) NULL;
IF COL_LENGTH(N'[AuditLogs]', N'ActorUserId') IS NULL             ALTER TABLE [AuditLogs] ADD [ActorUserId] uniqueidentifier NULL;
IF COL_LENGTH(N'[AuditLogs]', N'Category') IS NULL                ALTER TABLE [AuditLogs] ADD [Category] nvarchar(32) NULL;
IF COL_LENGTH(N'[AuditLogs]', N'CustomerId') IS NULL              ALTER TABLE [AuditLogs] ADD [CustomerId] uniqueidentifier NULL;
IF COL_LENGTH(N'[AuditLogs]', N'Importance') IS NULL              ALTER TABLE [AuditLogs] ADD [Importance] nvarchar(16) NULL;
IF COL_LENGTH(N'[AuditLogs]', N'IsRealtime') IS NULL              ALTER TABLE [AuditLogs] ADD [IsRealtime] bit NOT NULL DEFAULT CAST(0 AS bit);
IF COL_LENGTH(N'[AuditLogs]', N'OrderId') IS NULL                 ALTER TABLE [AuditLogs] ADD [OrderId] uniqueidentifier NULL;
IF COL_LENGTH(N'[AuditLogs]', N'ProductId') IS NULL               ALTER TABLE [AuditLogs] ADD [ProductId] uniqueidentifier NULL;
IF COL_LENGTH(N'[AuditLogs]', N'RealtimeDispatchedAtUtc') IS NULL ALTER TABLE [AuditLogs] ADD [RealtimeDispatchedAtUtc] datetime2 NULL;
IF COL_LENGTH(N'[AuditLogs]', N'SourceKey') IS NULL               ALTER TABLE [AuditLogs] ADD [SourceKey] nvarchar(180) NULL;
IF COL_LENGTH(N'[AuditLogs]', N'StaffUserId') IS NULL             ALTER TABLE [AuditLogs] ADD [StaffUserId] uniqueidentifier NULL;
IF COL_LENGTH(N'[AuditLogs]', N'Summary') IS NULL                 ALTER TABLE [AuditLogs] ADD [Summary] nvarchar(800) NULL;
IF COL_LENGTH(N'[AuditLogs]', N'Title') IS NULL                   ALTER TABLE [AuditLogs] ADD [Title] nvarchar(160) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_ActorUserId_TimestampUtc' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE INDEX [IX_AuditLogs_ActorUserId_TimestampUtc] ON [AuditLogs] ([ActorUserId], [TimestampUtc]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_Category_TimestampUtc_Id' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE INDEX [IX_AuditLogs_Category_TimestampUtc_Id] ON [AuditLogs] ([Category], [TimestampUtc], [Id]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_CustomerId_TimestampUtc' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE INDEX [IX_AuditLogs_CustomerId_TimestampUtc] ON [AuditLogs] ([CustomerId], [TimestampUtc]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_IsRealtime_RealtimeDispatchedAtUtc_TimestampUtc' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE INDEX [IX_AuditLogs_IsRealtime_RealtimeDispatchedAtUtc_TimestampUtc] ON [AuditLogs] ([IsRealtime], [RealtimeDispatchedAtUtc], [TimestampUtc]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_OrderId_TimestampUtc' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE INDEX [IX_AuditLogs_OrderId_TimestampUtc] ON [AuditLogs] ([OrderId], [TimestampUtc]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_ProductId_TimestampUtc' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE INDEX [IX_AuditLogs_ProductId_TimestampUtc] ON [AuditLogs] ([ProductId], [TimestampUtc]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_SourceKey' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE UNIQUE INDEX [IX_AuditLogs_SourceKey] ON [AuditLogs] ([SourceKey]) WHERE [SourceKey] IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_AuditLogs_StaffUserId_TimestampUtc' AND [object_id] = OBJECT_ID(N'[AuditLogs]'))
    CREATE INDEX [IX_AuditLogs_StaffUserId_TimestampUtc] ON [AuditLogs] ([StaffUserId], [TimestampUtc]);

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260922040450_AddBusinessActivityAudit', N'9.0.0');
IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260923120646_updatedMigration')
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260923120646_updatedMigration', N'9.0.0');

COMMIT;
GO
-- Afterwards run `update-database` (or start the app) so the remaining migrations
-- (20260923173005_AddOrderKind, 20260924002228_AddServiceFeeSnapshot) are applied.
