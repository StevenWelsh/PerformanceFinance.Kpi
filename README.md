# PerformanceFinance.Kpi

The internet-facing KPI site and its own database, `CMS_KPI`, filled one way from CMS. Nothing here changes
CMS (the desktop / web / hub solution); CMS's own KPI pages stay as they are until this replaces them.

| Project | Role |
|---|---|
| `Kpi.Data` | EF Core migrations for `CMS_KPI`: schemas, copied tables, staging tables, the publish procedure, the report procedures (ports of CMS's `WebKPI` ones), roles. |
| `Kpi.Tools` | Run by a person: `migrate` applies the migrations, `seed` creates the first System Admin of a new database, `compare` checks each report against CMS's `WebKPI` version. |
| `Kpi.Push` | The copy: reads the `KpiExport` views, bulk-loads `Stage.*` in `CMS_KPI`, then runs `Stage.spPublish` (one transaction, so reports never see half a load). `Frequent` every 10 minutes, `Nightly` also refreshes Bad Debt and Exposure. |
| `Kpi.Web` | The site: CMS.Web's KPI pages (shell, every report, phone views, the cache and 10-minute refresh), copied, reading `CMS_KPI` through the `Rpt` procedures; sign-in (ASP.NET Identity + a compulsory authenticator app, invitation only), My Account and Settings. |
| `Local/KPI_Export.sql` | A local stand-in for the `KpiExport` schema that will eventually live in CMS_Live: its own database, views word for word as they will be in CMS, reading `CMS_Dev` through synonyms. CMS_Dev itself is not changed. |

## First run on a developer machine

Connection strings live in user-secrets (your profile, outside the repo), never in a committed file. From the
repository folder:

```
dotnet user-secrets set "ConnectionStrings:CmsKpi" "Server=.\SQLEXPRESS;Database=CMS_KPI;Trusted_Connection=True;TrustServerCertificate=True" --project Kpi.Tools
dotnet run --project Kpi.Tools -- migrate
sqlcmd -S .\SQLEXPRESS -E -b -i Local\KPI_Export.sql
dotnet user-secrets set "ConnectionStrings:Source" "Server=.\SQLEXPRESS;Database=KPI_Export;Trusted_Connection=True;TrustServerCertificate=True" --project Kpi.Push
dotnet user-secrets set "ConnectionStrings:CmsKpi" "Server=.\SQLEXPRESS;Database=CMS_KPI;Trusted_Connection=True;TrustServerCertificate=True" --project Kpi.Push
dotnet run --project Kpi.Push -- Nightly
```

`migrate` creates `CMS_KPI` if it does not exist. It refuses a database on another server (Azure) unless given
`--remote`. Database changes are made only through migrations, run by a person.

## Running the site

```
dotnet user-secrets set "ConnectionStrings:CmsKpi" "Server=.\SQLEXPRESS;Database=CMS_KPI;Trusted_Connection=True;TrustServerCertificate=True" --project Kpi.Web
dotnet run --project Kpi.Tools -- seed --email <your email> --name "<your name>"
dotnet run --project Kpi.Web --launch-profile Kpi.Web
```

Then open https://localhost:7241. `seed` asks for the password (hidden) and makes the first System Admin; its first
sign-in sets up an authenticator app straight away (within the hour). Everyone else is invited from Settings > Users;
until Resend is connected in Settings > Integrations the invitation link is shown there to pass on by hand. Outside
the live site Resend sends only to its test address.

The KPI pages are copies of CMS.Web's (`CMS.Web/Components/Pages/Kpis`, `Components/Shared/Kpis`, `Data/Kpis`,
`wwwroot/app.css` and `css/kpi.css`), changed only where they fetch data: CMS.Core's `Kpis` services became C# services
of the same names in `Kpi.Web/Reports`, each running its `Rpt` procedure, and the filter lists come from
`Rpt.spFilterOptions`. Keep the two copies in step until one replaces the other. Exposure has no fund filter here
(its figures are last night's, worked out per lender).

## Checking the reports against CMS

`compare` runs every report as CMS produces it today (the `WebKPI` procedures, the desktop's Bad Debt and Exposure,
and the two SQL files CMS.Core embeds, read from the CMS repository beside this one) and the `Rpt` version in
`CMS_KPI`, for each filter value the pages offer, and lists any difference. Read-only on both sides; run it straight
after a push so both read the same data. Add a report name (or part of one) to run just that report.

```
dotnet user-secrets set "ConnectionStrings:Cms" "Server=.\SQLEXPRESS;Database=CMS_Dev;Trusted_Connection=True;TrustServerCertificate=True" --project Kpi.Tools
dotnet run --project Kpi.Tools -- compare
dotnet run --project Kpi.Tools -- compare "Margin"
```
