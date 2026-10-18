using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>Published links (Settings > Published links): one report, opened by URL without signing in.</summary>
    public partial class PublishedLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishedLink",
                schema: "Cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReportSlug = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    KeyHint = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAddress = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    UseCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedLink", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublishedLink_KeyHash",
                schema: "Cfg",
                table: "PublishedLink",
                column: "KeyHash",
                unique: true);

            // the site makes, revokes and stamps its links; it never deletes one (a revoked link keeps its history)
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON Cfg.PublishedLink TO kpi_site;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON Cfg.PublishedLink FROM kpi_site;");

            migrationBuilder.DropTable(
                name: "PublishedLink",
                schema: "Cfg");
        }
    }
}
