BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [ActivityType] nvarchar(80) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [ActorName] nvarchar(120) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [ActorRole] nvarchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [ActorUserId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [Category] nvarchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [CustomerId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [Importance] nvarchar(16) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [IsRealtime] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [OrderId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [ProductId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [RealtimeDispatchedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [SourceKey] nvarchar(180) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [StaffUserId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [Summary] nvarchar(800) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    ALTER TABLE [AuditLogs] ADD [Title] nvarchar(160) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_ActorUserId_TimestampUtc] ON [AuditLogs] ([ActorUserId], [TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_Category_TimestampUtc_Id] ON [AuditLogs] ([Category], [TimestampUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_CustomerId_TimestampUtc] ON [AuditLogs] ([CustomerId], [TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_IsRealtime_RealtimeDispatchedAtUtc_TimestampUtc] ON [AuditLogs] ([IsRealtime], [RealtimeDispatchedAtUtc], [TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_OrderId_TimestampUtc] ON [AuditLogs] ([OrderId], [TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_ProductId_TimestampUtc] ON [AuditLogs] ([ProductId], [TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_AuditLogs_SourceKey] ON [AuditLogs] ([SourceKey]) WHERE [SourceKey] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_StaffUserId_TimestampUtc] ON [AuditLogs] ([StaffUserId], [TimestampUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN

    UPDATE a SET
     OldValues = CASE WHEN EntityName IN ('Product','Category','MajorCategory','Inventory','Role','Review','Order','Payment') THEN
     (SELECT '{' + STRING_AGG(CONVERT(nvarchar(max), '"' + STRING_ESCAPE(j.[key], 'json') + '":' + CASE WHEN j.[type]=1 THEN '"' + STRING_ESCAPE(j.[value], 'json') + '"' WHEN j.[type]=0 THEN 'null' ELSE j.[value] END), ',') + '}'
      FROM OPENJSON(CASE WHEN ISJSON(a.OldValues)=1 THEN a.OldValues ELSE '{}' END) j
      WHERE j.[type] IN (0,1,2,3) AND j.[key] IN ('Name','CategoryId','MajorCategoryId','IsActive','IsFeatured','IsCustomizable','DisplayOrder','Status','OrderId','CustomerId','ProductId','Rating','IsApproved','QuantityOnHand','QuantityReserved','LowStockThreshold')) ELSE NULL END,
     NewValues = CASE WHEN EntityName IN ('Product','Category','MajorCategory','Inventory','Role','Review','Order','Payment') THEN
     (SELECT '{' + STRING_AGG(CONVERT(nvarchar(max), '"' + STRING_ESCAPE(j.[key], 'json') + '":' + CASE WHEN j.[type]=1 THEN '"' + STRING_ESCAPE(j.[value], 'json') + '"' WHEN j.[type]=0 THEN 'null' ELSE j.[value] END), ',') + '}'
      FROM OPENJSON(CASE WHEN ISJSON(a.NewValues)=1 THEN a.NewValues ELSE '{}' END) j
      WHERE j.[type] IN (0,1,2,3) AND j.[key] IN ('Name','CategoryId','MajorCategoryId','IsActive','IsFeatured','IsCustomizable','DisplayOrder','Status','OrderId','CustomerId','ProductId','Rating','IsApproved','QuantityOnHand','QuantityReserved','LowStockThreshold')) ELSE NULL END
    FROM AuditLogs a WHERE ActivityType IS NULL;

    IF NOT EXISTS (SELECT 1 FROM Permissions WHERE Name='activity.view')
     INSERT INTO Permissions (Id,Name,Description) VALUES (NEWID(),'activity.view','View business activity');
    INSERT INTO RolePermissions (Id,RoleId,PermissionId)
     SELECT NEWID(),r.Id,p.Id FROM Roles r CROSS JOIN Permissions p
     WHERE r.NormalizedName='OWNER' AND r.IsDeleted=0 AND p.Name='activity.view'
     AND NOT EXISTS(SELECT 1 FROM RolePermissions rp WHERE rp.RoleId=r.Id AND rp.PermissionId=p.Id);

END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260922040450_AddBusinessActivityAudit'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260922040450_AddBusinessActivityAudit', N'9.0.0');
END;

COMMIT;
GO

