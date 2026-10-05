BEGIN TRANSACTION;
ALTER TABLE [Categories] ADD [MajorCategoryId] uniqueidentifier NULL;

CREATE TABLE [MajorCategories] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(120) NOT NULL,
    [NormalizedName] nvarchar(120) NOT NULL,
    [Description] nvarchar(1000) NULL,
    [DisplayOrder] int NOT NULL,
    [CreatedDateUtc] datetime2 NOT NULL,
    [CreatedBy] nvarchar(max) NULL,
    [ModifiedDateUtc] datetime2 NULL,
    [ModifiedBy] nvarchar(max) NULL,
    [IsDeleted] bit NOT NULL,
    [DeletedDateUtc] datetime2 NULL,
    [DeletedBy] nvarchar(max) NULL,
    CONSTRAINT [PK_MajorCategories] PRIMARY KEY ([Id])
);

CREATE INDEX [IX_Categories_MajorCategoryId] ON [Categories] ([MajorCategoryId]);

CREATE UNIQUE INDEX [IX_MajorCategories_NormalizedName] ON [MajorCategories] ([NormalizedName]) WHERE [IsDeleted] = 0;

ALTER TABLE [Categories] ADD CONSTRAINT [FK_Categories_MajorCategories_MajorCategoryId] FOREIGN KEY ([MajorCategoryId]) REFERENCES [MajorCategories] ([Id]) ON DELETE NO ACTION;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260922025355_AddMajorCategories', N'9.0.0');

COMMIT;
GO

