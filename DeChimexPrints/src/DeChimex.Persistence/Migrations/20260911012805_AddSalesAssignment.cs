using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedSalesName",
                table: "Orders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedSalesUserId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConversationState",
                table: "Orders",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AssignedSalesUserId",
                table: "Orders",
                column: "AssignedSalesUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_AssignedSalesUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AssignedSalesName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AssignedSalesUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ConversationState",
                table: "Orders");
        }
    }
}
