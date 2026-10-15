using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// A tick per report (Oct 2026, Steve): each User sees the reports chosen for them in Settings > Users, or every report
    /// (AllReports, including any added later). Replaces the Bad Debt / Exposure ticks and the Admin role ("a User with
    /// every report"). Existing accounts keep what they could see: a User the standard reports plus their two ticks, an
    /// Admin everything.
    /// </summary>
    public partial class ReportAccess : Migration
    {
        private const string StandardReports =
            "sales-summary,year-on-year,salesperson-summary,sales-performance,sales-productivity,record-performances," +
            "suppliers-yoy,supplier-development,business-analysis,heat-map,margin-analysis,minimum-irr";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllReports",
                schema: "Auth",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Reports",
                schema: "Auth",
                table: "Users",
                type: "varchar(600)",
                unicode: false,
                maxLength: 600,
                nullable: true);

            // what each account could see stays what it can see
            migrationBuilder.Sql($"""
UPDATE Auth.Users
SET    Reports = '{StandardReports}'
             + CASE WHEN CanViewBadDebt  = 1 THEN ',bad-debt' ELSE '' END
             + CASE WHEN CanViewExposure = 1 THEN ',exposure' ELSE '' END;
""");

            // the Admin role becomes "a User with every report"
            migrationBuilder.Sql("""
DECLARE @admin nvarchar(450) = (SELECT Id FROM Auth.Roles WHERE NormalizedName = 'ADMIN');
DECLARE @user  nvarchar(450) = (SELECT Id FROM Auth.Roles WHERE NormalizedName = 'USER');
IF @admin IS NOT NULL
BEGIN
    UPDATE U SET AllReports = 1
    FROM   Auth.Users U
           INNER JOIN Auth.UserRoles R ON R.UserId = U.Id AND R.RoleId = @admin;
    IF @user IS NOT NULL
        UPDATE Auth.UserRoles SET RoleId = @user WHERE RoleId = @admin;
    DELETE Auth.Roles WHERE Id = @admin;
END
""");

            migrationBuilder.DropColumn(
                name: "CanViewBadDebt",
                schema: "Auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanViewExposure",
                schema: "Auth",
                table: "Users");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanViewBadDebt",
                schema: "Auth",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanViewExposure",
                schema: "Auth",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // back to the two ticks (the Admin role is not recreated: everyone with every report keeps both ticks)
            migrationBuilder.Sql("""
UPDATE Auth.Users
SET    CanViewBadDebt  = CASE WHEN AllReports = 1 OR ',' + ISNULL(Reports, '') + ',' LIKE '%,bad-debt,%' THEN 1 ELSE 0 END,
       CanViewExposure = CASE WHEN AllReports = 1 OR ',' + ISNULL(Reports, '') + ',' LIKE '%,exposure,%' THEN 1 ELSE 0 END;
""");

            migrationBuilder.DropColumn(
                name: "Reports",
                schema: "Auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AllReports",
                schema: "Auth",
                table: "Users");
        }
    }
}
