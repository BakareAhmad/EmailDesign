using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Orders",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Custom");

            // Backfill legacy rows (column default is "Custom", so custom-workflow rows
            // are already correct): flip rows with NO custom-workflow children and NO
            // custom pre-payment history to "Direct" — those were cart/checkout purchases.
            // (DIRECT_CHECKOUT_EVALUATION.md §10.)
            migrationBuilder.Sql(@"
UPDATE Orders SET Kind = 'Direct'
WHERE Status NOT IN ('AwaitingReview','InDiscussion','RequirementsConfirmed','AwaitingDesignApproval')
  AND NOT EXISTS (SELECT 1 FROM OrderRequirementResponses r WHERE r.OrderId = Orders.Id)
  AND NOT EXISTS (SELECT 1 FROM OrderCustomLines c WHERE c.OrderId = Orders.Id)
  AND NOT EXISTS (SELECT 1 FROM OrderFiles f WHERE f.OrderId = Orders.Id)
  AND NOT EXISTS (SELECT 1 FROM OrderDesignProofs p WHERE p.OrderId = Orders.Id)
  AND NOT EXISTS (SELECT 1 FROM OrderDesignReviews d WHERE d.OrderId = Orders.Id)
  AND NOT EXISTS (SELECT 1 FROM OrderTimelineEntries t WHERE t.OrderId = Orders.Id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Orders");
        }
    }
}
