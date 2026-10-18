using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublishedLinkHeader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowHeader",
                schema: "Cfg",
                table: "PublishedLink",
                type: "bit",
                nullable: false,
                defaultValue: true);   // existing links keep showing their header
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowHeader",
                schema: "Cfg",
                table: "PublishedLink");
        }
    }
}
