using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeveloperPortalDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Integration = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Template = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RecipientCategory = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RecipientMasked = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestMetricBuckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BucketStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Route = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestCount = table.Column<int>(type: "int", nullable: false),
                    ServerErrorCount = table.Column<int>(type: "int", nullable: false),
                    ClientErrorCount = table.Column<int>(type: "int", nullable: false),
                    SlowCount = table.Column<int>(type: "int", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    MaxDurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestMetricBuckets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TechnicalEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    MessageTemplate = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Fingerprint = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StackTrace = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Route = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Integration = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Environment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicalEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCalls_Integration_Operation_TimestampUtc",
                table: "IntegrationCalls",
                columns: new[] { "Integration", "Operation", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCalls_Integration_TimestampUtc",
                table: "IntegrationCalls",
                columns: new[] { "Integration", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCalls_Reference",
                table: "IntegrationCalls",
                column: "Reference");

            migrationBuilder.CreateIndex(
                name: "IX_RequestMetricBuckets_BucketStartUtc_Method_Route",
                table: "RequestMetricBuckets",
                columns: new[] { "BucketStartUtc", "Method", "Route" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvents_Category_TimestampUtc",
                table: "TechnicalEvents",
                columns: new[] { "Category", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvents_CorrelationId",
                table: "TechnicalEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvents_Fingerprint_TimestampUtc",
                table: "TechnicalEvents",
                columns: new[] { "Fingerprint", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvents_Severity_TimestampUtc",
                table: "TechnicalEvents",
                columns: new[] { "Severity", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalEvents_TimestampUtc",
                table: "TechnicalEvents",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationCalls");

            migrationBuilder.DropTable(
                name: "RequestMetricBuckets");

            migrationBuilder.DropTable(
                name: "TechnicalEvents");
        }
    }
}
