using Kpi.Data.Shire;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Shire Business Analysis (3 Nov 2026, Steve): four more of each won deal's fields in the Shire copy (sector,
    /// finance product, loan purpose, funder) and the report's two procedures. The SQL is
    /// Kpi.Data/Shire/002_ShireBusinessAnalysis.sql; this migration runs it batch by batch. No EF model change.
    /// </summary>
    public partial class ShireBusinessAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in ShireScripts.Batches("002_ShireBusinessAnalysis.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in ShireScripts.Batches("002_ShireBusinessAnalysis_Down.sql"))
                migrationBuilder.Sql(batch);
        }
    }
}
