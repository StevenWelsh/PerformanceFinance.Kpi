# PerformanceFinance.Kpi — project guide for Claude

**The internet-facing KPI website (kpi.performancefinance.co.uk) and its own database, `CMS_KPI`.** Shared context
for everyone on this repo; Claude loads it every session. Record durable, team-relevant facts here (and tell the
user); ask when unsure whether something belongs. Personal notes go in `CLAUDE.local.md` or `~/.claude`.

**"CMS" means the whole CMS solution** (`C:\GitRepo\performance-finance-cms`: desktop, CMS.Core, CMS.Models, CMS.Web,
the hub). This repository is separate on purpose and must not change CMS. The one thing that will eventually go into
CMS is the `KpiExport` schema (below), through a CMS.Models migration when the user decides.

## Why it exists (Oct 2026, Steve)

CMS.Web must stay internal (IP-restricted), but the KPIs are wanted on the internet. So the KPI site runs in Azure
against its own database holding only what the reports show, filled ONE WAY from CMS. A break-in reaches KPI
figures, with no route into the office. Inside the office KPIs stay on CMS.Web `/kpis` with the normal CMS sign-in.
Plan, object by object: `docs/analytics/kpi-database-plan.html` in the CMS repo
(https://claude.ai/artifact/RfmeWhd66Z693rkmzpkgG9).

## Layout

| Project | Role |
|---|---|
| `Kpi.Data` | EF Core 8 migrations for `CMS_KPI` (history table `Ref.MigrationsHistory`). The schema is SQL in `migrationBuilder.Sql(...)`, not an EF model: there are no entity classes for the report tables. |
| `Kpi.Tools` | Run by a person: `migrate`, `seed --email --name` (the first System Admin), `grant-site --app --app-id` (a web app's managed identity into `kpi_site`), each with `--azure` for the Azure database (`CmsKpiAzure`; a non-local server is refused without it); `compare [report]`. |
| `Kpi.Push` | The copy (net8.0-windows console): `Frequent` every 10 minutes, `Nightly` also refreshes Bad Debt and Exposure; `--azure` copies into `CmsKpiAzure` from a developer's machine. |
| `Kpi.Web` | The site: Blazor Server, CMS.Web's KPI pages copied, reading `CMS_KPI`. |
| `Local/KPI_Export.sql` | Local stand-in for CMS_Live's `KpiExport` schema (see below). |

Connection strings live in **user-secrets** (each csproj has a `UserSecretsId` label) or environment variables,
never in committed files: `Kpi.Tools` needs `CmsKpi` and `Cms` (for compare); `Kpi.Push` needs `Source` and
`CmsKpi`; `Kpi.Web` needs `CmsKpi`. Local values: `Server=.\SQLEXPRESS;Database=<db>;Trusted_Connection=True;TrustServerCertificate=True`.

## The data flow

`KpiExport` views (in CMS_Live eventually; locally the `KPI_Export` database) → `Kpi.Push` bulk-copies each into
`Stage.*` in `CMS_KPI` → `Stage.spPublish` swaps everything into `Dim`/`Fact` in ONE transaction (read-committed
snapshot, so a report never sees half a load; it refuses an empty `Stage.Lead`) → the site runs only `Rpt.*`.
Every load replaces the tables in full (25k paid leads, ~90 s for a nightly load locally).

- **Schemas in CMS_KPI:** `Stage` (landing + `spPublish`), `Ref` (`Setting` report date and load times, `LoadRun`
  log, EF history), `Cfg` (KPI-owned settings: `SalespersonScope`, `PostcodeArea`; later `Integration`), `Dim`, `Fact`
  (Bad Debt / Exposure are last night's results of the desktop's own procedures), `Rpt` (the report procedures,
  ports of CMS's WebKPI ones), `Auth` (the site's own sign-in: Identity's tables as `Auth.Users` / `Roles` / ...,
  and the append-only `Auth.AuditEntry`), `Cfg.Integration` (Resend). Nothing about CMS's users is copied: KPI rights are
  set on the KPI site (Oct 2026, Steve).
- **Roles:** `kpi_site` (EXECUTE `Rpt`; read/write its own `Auth` tables and `Cfg.Integration`, with UPDATE/DELETE on
  `Auth.AuditEntry` DENIED), `kpi_push` (fill `Stage`, run `spPublish` and `spRecordFailure`, nothing else), `kpi_reader`
  (EXECUTE `Rpt`). Logins/users are added by hand per server.
- **The copy logs every failure it can:** `Stage.spPublish` logs its own; a copy that fails BEFORE publishing (reading
  CMS, the bulk copy) calls `Stage.spRecordFailure` if it can still reach CMS_KPI. Settings > Data sync shows
  `Ref.LoadRun` (`Rpt.spLoadHistory`) and the overdue flags (`Rpt.spDataStatus`); a copy that can't reach CMS_KPI at all
  shows only as Overdue (then: the office server's scheduled task and its Windows event log, source `Kpi.Push`).
- **"As at" on every report = the business date AND the time of the last copy** (`KpiDataStatus`, Rpt.spDataStatus,
  cached a minute; full view and phone bar), so a reader sees how fresh the figures are; amber with a warning when the
  copy is overdue. A past year's figures (CMS's current year set back) show the date alone. "Refreshed" is only when
  that browser last fetched.
- **Local stand-in:** `KPI_Export` holds the views word for word as they will be in CMS, reading CMS_Dev through
  SYNONYMS (`Lead.vLead` there = `CMS_Dev.Lead.vLead`), so moving them into CMS is a copy. Rerunnable (drops and
  recreates its objects); `@CmsDb` at the top picks the CMS database. Created by the user, never by Claude.
- **Exposure has no fund filter on the KPI site (Oct 2026, Steve):** nightly runs = 3 groupings × (all lenders +
  each lender), a few minutes. Per fund would be ~300 more runs a night (830 funds in CMS_Dev, ~300 with book).

## Rules

- **Database changes go through migrations in `Kpi.Data`, run by the USER**: Visual Studio's Package Manager Console
  `Update-Database` (Default project Kpi.Data), or `dotnet run --project Kpi.Tools -- migrate`. As DavidsonHR, the EF
  tools need `Microsoft.EntityFrameworkCore.Tools` in Kpi.Data (the console's commands; Design alone serves only
  `dotnet ef`) and Design in the startup project (Kpi.Web), and the design-time factory opens ONLY the local CMS_KPI
  (`KPI_DEV_SERVER` changes the server) - so a stray Update-Database can never reach Azure, which only
  `Kpi.Tools migrate --azure` does. Claude never applies DB changes directly, to `CMS_KPI`, `KPI_Export` or any CMS database (read-only
  queries are fine). Once a migration is applied anywhere, fix forward with a new one; don't edit it.
- **After changing any `Rpt` procedure, run `compare`** against CMS (241 cases, ~4 min, read-only): the site's
  figures must equal CMS.Web's for every filter value. All 241 matched on 15 Oct 2026.
- **`Kpi.Web`'s KPI pages are COPIES of CMS.Web's** (`Components/Pages/Kpis`, `Components/Shared/Kpis`, `Data/Kpis`,
  `wwwroot/app.css`, `css/kpi.css`, heat map assets), changed only where they fetch data, so the two stay easy to
  diff: CMS.Core's `Kpis` services became C# services of the SAME names and signatures in `Kpi.Web/Reports`, the
  pages still say `using var cms = await SessionFactory.BeginAsync(); ... cms.Db` (a `KpiDb`, Dapper over
  `Rpt.*`), and filter lists come from `Rpt.spFilterOptions` (`cms.Db.Options("Divisions")`). A change to a KPI page
  belongs in BOTH copies until one replaces the other. The pages keep CMS.Web's policy names
  (`Dashboard.View`, `OBEnquiryBadDebt.View`, `OBEnquiryExposure.View`).
- **The site reads REPORT data only through `Rpt` procedures** (Dapper, `KpiDb`) — never a copied table directly; a new
  list or figure is a new `Rpt` procedure (a migration). The site's OWN tables (`Auth.*`, `Cfg.Integration`) go
  through EF Core (`KpiDbContext`).
- **KPI passwords stay separate from desktop passwords** — never make this database part of the desktop sign-in.

## Sign-in, My Account, Settings (built Oct 2026, as DavidsonHR `C:\GitRepo\DavidsonHR`)

- **ASP.NET Core Identity on EF Core** (`KpiUser` in `Auth.Users`), a COMPULSORY authenticator app (TOTP, QR by QRCoder),
  no recovery codes, **invitation only**. This departs from the CMS hub's "own tables, no Identity" rule deliberately:
  that rule exists so staff and customers share one implementation in CMS.Core (which also builds for the net48
  desktop); this site is standalone net8.
- **Roles (one each), and who sees which report (Oct 2026, Steve):** **SystemAdmin** — every report, Settings, and ALL
  user management ("make it all system admin's job"); **User** — the reports ticked for them in Settings > Users, or
  "All reports, including new ones" (`KpiUser.AllReports` / `Reports`, slugs comma separated). The Admin role went:
  it was "a User with every report". **A report someone can't see is HIDDEN** — sidebar, phone menu, My Account's
  favourites — **and a heading with none left goes too**; opening its address is refused. Settings is the System
  Admin's alone (no lock icons: nobody else has any section).
- **One catalogue, `Data/Kpis/KpiReports`** (slug, label, icon, section): the menus, the favourites, the report ticks and
  the check all read it. **A new report page must be added there**, or only "all reports" people see it.
- **The check needs no change to the copied pages:** they keep CMS.Web's `[Authorize(Policy = "Dashboard.View")]`, and
  `ReportHandler` decides that policy from the page being opened (its address on an ordinary request, its route
  template when Blazor navigates) against the `kpi:reports` claim (`*` = everything). `OBEnquiryBadDebt.View` /
  `OBEnquiryExposure.View` are the same check for those two slugs. No request touches the database to authorise.
- **Two-factor is enforced three ways:** `AccountGate` middleware (runs BEFORE authorization, so a half-set-up account is
  sent to finish rather than told "no access"), every policy requires `kpi:2fa = on` (covers the live Blazor connection),
  and **a password alone NEVER leads to authenticator setup** — only an emailed link (invitation, reset, re-enrol) or
  My Account's new-phone form (needs the password inside a signed-in session) opens the ONE-HOUR setup window
  (`AccountSetup`: random value in a `__Host-` cookie, its hash on the user row; `ExpireSetupsTask` closes stale ones and
  clears an unactivated account's password). The one exception: the account made by `Kpi.Tools seed` (`IsSeeded`), on
  its first sign-in, because a new database has no email yet.
- **Sign-in pages are STATIC SSR under `/account`** (App.razor picks the render mode by path) posting plain forms to
  `AccountEndpoints` (minimal API: the cookie can only be written in an ordinary request). Every POST is antiforgery
  checked (form binding infers it; logout reads no field, so it carries `RequireAntiforgeryTokenAttribute`), rate
  limited per IP, and a refused sign-in always gets the SAME message. The audit (`Auth.AuditEntry`) records sign-ins,
  refusals, invitations and changes; append only (context guard + DENY).
- **Sessions:** security stamp checked every minute; `KpiAuthStateProvider` re-checks open pages every minute — a
  disable, password change or role change (stamp roll) ends the session; changed report rights arrive WITHOUT a
  sign-out.
- **My Account** (`/my-account`, its own page, phone friendly — Steve): profile, favourite reports, change password, new
  phone. **Favourites are kept with the LOGIN** (`KpiUser.FavouriteReports`, `FavouritesMenuView`), so every device has
  them — CMS.Web keeps them in the phone's browser — and the **desktop sidebar has the phone menu's Favourites / All
  switch and a star on every report** (Steve), the same choice on both; with a mouse an unstarred star shows only while
  hovering the sidebar. The star is one gold everywhere (`--kpim-star`,
  defined in kpi.css AND account.css with the same value: kpi.css loads only on report pages). **Avatar menu** top
  right of every report, after the docked-right "As at / Refreshed" lines (`KpiUserMenu`; on a phone in the reports
  menu's head, turned white-on-crimson there): My Account, Settings (System Admin only), Log out - the ONLY way to
  them; the sidebar holds reports alone (Steve).
- **Settings** (`/settings`, System Admin only): Users (`UserAdmin` — invite, edit role/rights, send again / withdraw,
  reset authenticator with "how did you check it was them?", disable), Integrations (Resend: key encrypted with Data
  Protection, never shown back, blank keeps it; a TEST ADDRESS catches every email, and outside Production nothing is
  sent without one), Data sync, Audit. When an email can't be sent the invitation / reset link is shown to the System
  Admin to pass on.
- **Data Protection key ring** encrypts cookies, emailed links and the Resend key: locally the user profile; **in Azure it
  must be a shared persisted ring (storage + Key Vault)** or every restart signs everyone out and the Resend key becomes
  unreadable. Pin `AllowedHosts` in Azure: emailed links are built from the request's host.

## Azure (LIVE for testing, 16 Oct 2026)

In Performance Finance's subscription ("Azure subscription 1", tenant `5917b69c-...`), resource group **`attachment-prod`**
(Steve is Contributor there only; `rg-pf-apps` was never created), UK West - generic, so other apps can share them:

- **SQL server `pf-sql-uk`**: Entra-only, admin = group **PF Database Admins** (Steve, Andy). Firewall: the web app's
  31 possible outbound IPs + developer IPs. **`CMS_KPI`**: Standard S0, CMS_Live's collation. Migrated, seeded and
  filled with `Kpi.Tools ... --azure` / `Kpi.Push ... --azure` (secrets `ConnectionStrings:CmsKpiAzure`,
  `Authentication=Active Directory Interactive`).
- **App Service plan `plan-pf-apps`** (B1 Linux) + **web app `pf-kpi`**: .NET 8, Always On, WebSockets, HTTPS only, system
  managed identity (app id `61d59e76-...`, a user in `kpi_site` by `grant-site`). App settings: `ASPNETCORE_ENVIRONMENT`
  Production, `AllowedHosts` (the two kpi domains + `pf-kpi.azurewebsites.net`), `ConnectionStrings__CmsKpi`
  (`Authentication=Active Directory Managed Identity;Encrypt=Strict`), `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (Azure
  ends HTTPS in front of the app; without it the site thinks it is on http and builds http:// links). Container logs to
  the filesystem (`/home/LogFiles/*_default_docker.log`, through Kudu).
- **Domains:** `kpi.fendra.co.uk` live (123-reg CNAME `kpi` → `pf-kpi.azurewebsites.net` + TXT `asuid.kpi` = the app's
  Custom Domain Verification ID; free App Service certificate, SNI, renews itself). `kpi.performancefinance.co.uk`: the
  same two records, given to PF's DNS people - its old A record (their IIS server) has to go first.
- **Deploying** (no pipeline yet): `dotnet publish Kpi.Web -c Release`, zip with FORWARD-SLASH paths (Windows
  PowerShell 5.1's `Compress-Archive` writes backslashes, which Linux unpacks as flat file names), then POST it to
  `https://pf-kpi.scm.azurewebsites.net/api/publish?type=zip&clean=true&restart=true` with a bearer token from
  `az account get-access-token --subscription "Azure subscription 1"`. **`clean=true` empties the site first: never upload
  if the publish failed** - an empty zip took the site down for 10 minutes on 15 Oct.
- **Still before go-live:** the Data Protection key ring is the App Service default (`/home`, survives restarts, not
  encrypted at rest) - wrap it with Key Vault; the office server's scheduled `Kpi.Push` (today's figures were pushed by
  hand from a developer's local `KPI_Export`).

## Gotchas met building it

- **EF Core creates the history table's schema (`Ref`) BEFORE the first migration runs**, so `CREATE SCHEMA Ref` in the
  migration failed ("There is already an object named 'Ref'"). Schemas are created `IF SCHEMA_ID(...) IS NULL`.
- **`SqlBulkCopy` truncates surplus decimal places instead of rounding**, and fails on over-long text with an obscure
  message. So `Stage` tables are WIDE (`nvarchar(max)`, `decimal(38,12)`, all nullable) and `spPublish`'s
  `INSERT ... SELECT` converts with SQL Server's own rules (rounds; over-long text stops the publish with a plain error).
- **A window function sees only the rows left after WHERE**: `Rpt.spSalesProductivity` numbered and totalled after
  filtering to one salesperson, so their "all salespeople" total was their own. Total in a CTE, filter outside.
- **sqlcmd reads `/` as an option**: give `-i` a quoted backslash path.
- **A fixed `<title>` in App.razor hides every static page's `<PageTitle>`** (both are written, the browser takes the
  first) — the CMS hub's trap again, on the `/account` pages. App.razor has none; every page sets its own.
- **The browser preview reads `.claude/launch.json` from the folder the session STARTED in**; a session moved here
  from the CMS repo still sees CMS's. Run the site in a terminal tab and attach the preview by URL
  (`http://localhost:5241`).
- **The Azure CLI takes the TENANT from its DEFAULT account, not from `--subscription`, in some commands** (Steve's default
  is another organisation). `az sql server create` / `ad-admin create` saved the Entra admin against the wrong
  directory, so every sign-in to `pf-sql-uk` went there ("Selected user account does not exist in tenant 'Default
  Directory'"); `az webapp deploy` got a 403 from Kudu the same way. Set the admin with the PF subscription made the
  default (`az account set`, then set it back), and deploy with your own token (above). Check
  `administrators.tenantId` on any new server.
- **The site's FIRST database connection after a start is slow in App Service (8-32 s; measured inside the container: the
  first TLS handshake and the first managed identity token, each fast after), and Azure SQL resets a connection that
  waits that long** - "Connection reset by peer" / "error: 35", the site's "Something went wrong". Ruled out: firewall,
  encryption mode, driver, network. Handled by retrying a dropped connection (`Kpi.Data.KpiRetryStrategy` for EF,
  `KpiDb.Open` for Dapper); the start-up `ExpireSetupsTask` run takes the slow one.
- **Ctrl+C on `dotnet run` can leave the program running** (a seed waiting at `Password:`): its exe then locks
  `bin/` and every build fails with MSB3027 until it is stopped. Answer the seed's prompts promptly - its Azure sign-in
  expires.
