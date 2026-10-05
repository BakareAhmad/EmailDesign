using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceFeeSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OrderTotal",
                table: "Receipts",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "OrderTotalCurrency",
                table: "Receipts",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "ServiceFee",
                table: "Receipts",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ServiceFeeCurrency",
                table: "Receipts",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "OrderAmount",
                table: "Payments",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PaystackFeeActual",
                table: "Payments",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PaystackFeeExpected",
                table: "Payments",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ServiceFeePercent",
                table: "Payments",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            // History backfill (brief §4): reconstruct each existing record's actual
            // economics from its own stored facts — never from today's configuration.
            // Pre-service-fee behaviour was the full pass-through: the customer-borne
            // charge was 100% of Paystack's fee. PaystackFeeActual stays NULL (it was
            // not captured before this feature).
            migrationBuilder.Sql(@"
UPDATE Payments SET OrderAmount = Amount - CustomerBorneCharge,
    ServiceFeePercent = CASE WHEN CustomerBorneCharge > 0 THEN 100 ELSE 0 END,
    PaystackFeeExpected = CustomerBorneCharge
WHERE OrderAmount = 0;

UPDATE r SET ServiceFee = p.CustomerBorneCharge, ServiceFeeCurrency = r.Currency,
    OrderTotal = p.Amount - p.CustomerBorneCharge, OrderTotalCurrency = r.Currency
FROM Receipts r INNER JOIN Payments p ON p.Reference = r.PaymentReference;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrderTotal",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "OrderTotalCurrency",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "ServiceFee",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "ServiceFeeCurrency",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "OrderAmount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PaystackFeeActual",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PaystackFeeExpected",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ServiceFeePercent",
                table: "Payments");
        }
    }
}
