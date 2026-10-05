using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGalleryPostCarousel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "GalleryMedia",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GalleryMedia_ParentId",
                table: "GalleryMedia",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_GalleryMedia_GalleryMedia_ParentId",
                table: "GalleryMedia",
                column: "ParentId",
                principalTable: "GalleryMedia",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GalleryMedia_GalleryMedia_ParentId",
                table: "GalleryMedia");

            migrationBuilder.DropIndex(
                name: "IX_GalleryMedia_ParentId",
                table: "GalleryMedia");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "GalleryMedia");
        }
    }
}
