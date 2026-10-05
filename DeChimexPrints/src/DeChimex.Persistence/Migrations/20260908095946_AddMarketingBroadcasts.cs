using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketingBroadcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MarketingSubscribed",
                table: "Customers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnsubscribeToken",
                table: "Customers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill: existing customers must hold distinct tokens before the
            // unique index is created (the entity assigns Guid.NewGuid() for all
            // new rows; this only repairs pre-marketing rows).
            migrationBuilder.Sql(
                "UPDATE [Customers] SET [UnsubscribeToken] = NEWID() WHERE [UnsubscribeToken] = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.CreateTable(
                name: "MarketingBroadcasts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HtmlContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PlainTextContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    QueuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ModifiedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingBroadcasts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketingBroadcastRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BroadcastId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingBroadcastRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketingBroadcastRecipients_MarketingBroadcasts_BroadcastId",
                        column: x => x.BroadcastId,
                        principalTable: "MarketingBroadcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_UnsubscribeToken",
                table: "Customers",
                column: "UnsubscribeToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketingBroadcastRecipients_BroadcastId_CustomerId",
                table: "MarketingBroadcastRecipients",
                columns: new[] { "BroadcastId", "CustomerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketingBroadcastRecipients_Status",
                table: "MarketingBroadcastRecipients",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketingBroadcastRecipients");

            migrationBuilder.DropTable(
                name: "MarketingBroadcasts");

            migrationBuilder.DropIndex(
                name: "IX_Customers_UnsubscribeToken",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "MarketingSubscribed",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "UnsubscribeToken",
                table: "Customers");
        }
    }
}
