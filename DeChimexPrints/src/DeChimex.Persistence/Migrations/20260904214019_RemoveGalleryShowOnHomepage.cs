using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveGalleryShowOnHomepage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GalleryMedia_ShowOnHomepage",
                table: "GalleryMedia");

            migrationBuilder.DropColumn(
                name: "ShowOnHomepage",
                table: "GalleryMedia");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowOnHomepage",
                table: "GalleryMedia",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_GalleryMedia_ShowOnHomepage",
                table: "GalleryMedia",
                column: "ShowOnHomepage");
        }
    }
}
