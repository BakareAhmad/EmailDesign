using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <summary>
    /// Adds the Payment refund columns (RefundReference, RefundedAtUtc) that the
    /// PaymentGatewayRefundCoordinator flow requires. These entity properties were
    /// added to the model on 2026-09-18 WITHOUT a migration (the delivered change set
    /// also never regenerated DeChimexDbContextModelSnapshot.cs — both repaired here:
    /// this migration carries the real schema delta; the snapshot file was regenerated
    /// to match the current model, which is identical to the target model of this
    /// migration and of 20260918100000_AddDesignReviewAttachments combined).
    /// </summary>
    public partial class AddPaymentRefundColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefundReference",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedAtUtc",
                table: "Payments",
                type: "datetime2",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefundReference",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundedAtUtc",
                table: "Payments");
        }
    }
}
