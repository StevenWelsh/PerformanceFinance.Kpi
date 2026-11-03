using Kpi.Data.Shire;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Shire Salesforce (3 Nov 2026, Steve): schema Shire - the won deals copied from Shire's Salesforce, the copy's
    /// staging, status and log, and the two Shire reports' procedures. The SQL is Kpi.Data/Shire/001_ShireSalesforce.sql
    /// (Steve: queries kept in one obvious place for review); this migration runs it batch by batch. No EF model change.
    /// </summary>
    public partial class ShireSalesforce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in ShireScripts.Batches("001_ShireSalesforce.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in ShireScripts.Batches("001_ShireSalesforce_Down.sql"))
                migrationBuilder.Sql(batch);
        }
    }
}
