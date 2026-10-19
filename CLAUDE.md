# PerformanceFinance.Kpi — project guide for Claude

**The internet-facing KPI website (kpi.performancefinance.co.uk) and its own database, `CMS_KPI`.** Shared context
for everyone on this repo; Claude loads it every session. Record durable, team-relevant facts here (and tell the
user); ask when unsure whether something belongs. Personal notes go in `CLAUDE.local.md` or `~/.claude`.

**"CMS" means the whole CMS solution** (`C:\GitRepo\performance-finance-cms`: desktop, CMS.Core, CMS.Models, CMS.Web,
the hub). This repository is separate on purpose; the ONE part of the KPI site that lives in CMS is the **`KpiExport`
schema** in the CMS database (below) - CMS repo migration `mgKpiExport`, readable copies in `CMS.Models/Scripts/KpiExport/`
(Oct 2026, Steve: authored in CMS so a CMS change that would break it is seen there). Any other CMS change is the user's call.

## Why it exists (Oct 2026, Steve)

CMS.Web must stay internal (IP-restricted), but the KPIs are wanted on the internet. So the KPI site runs in Azure
against its own database holding only what the reports show, filled ONE WAY from CMS. A break-in reaches KPI
figures, with no route into the office. Inside the office KPIs stay on CMS.Web `/kpis` with the normal CMS sign-in.
Plan, object by object: `docs/analytics/kpi-database-plan.html` in the CMS repo
(https://claude.ai/artifact/RfmeWhd66Z693rkmzpkgG9).

## Layout

| Project | Role |
|---|---|
| `Kpi.Data` | EF Core 10 migrations for `CMS_KPI` (history table `Ref.MigrationsHistory`). The schema is SQL in `migrationBuilder.Sql(...)`, not an EF model: there are no entity classes for the report tables. |
| `Kpi.Tools` | Run by a person: `migrate`, `seed --email --name` (the first System Admin), `grant-site --app --app-id` (a web app's managed identity into `kpi_site`), `grant-push --app --app-id` (an Entra identity - the site's, which copies - into `kpi_push`), each with `--azure` for the Azure database (`CmsKpiAzure`; a non-local server is refused without it); `export-apply --cms <db>` / `export-check` / `grant-pull --user <name>` (KPI_Export: server = `ConnectionStrings:Export`, `--remote` to change another server; grant-pull makes the site's SQL login there with a NEW password shown once); `compare [report]`. |
| `Kpi.Copy` | The copy itself, shared by the site and Kpi.Push: `KpiCopier` (read `KpiCopy.Refresh`, copy `KpiCopy.*` into `Stage`, publish) and `Export/` - the KPI_Export script and `ExportVersion`. |
| `Kpi.Push` | The copy from a DEVELOPER'S machine: refreshes the local KPI_Export as the office's Agent jobs would (`Nightly` prepares Bad Debt and Exposure first; `--no-refresh` skips), then runs Kpi.Copy into the LOCAL CMS_KPI - it refuses any other server (its `--azure` went 18 Oct 2026: a CMS_Dev copy must never overwrite the live site's figures). Not used in production. |
| `Kpi.Web` | The site: Blazor Server, CMS.Web's KPI pages copied, reading `CMS_KPI`; `Data/KpiPull` is the production copy. |

**.NET 10 (Oct 2026):** every project targets .NET 10 (LTS to Nov 2028; .NET 8 support ends 10 Nov 2026). EF Core and
Identity 10.0.x, `Microsoft.Data.SqlClient` **6.1.x** - the version EF Core 10 is built on; 7.x moves Entra sign-in to a
separate package, which would break the site's managed identity. `System.Security.Cryptography.Pkcs` is pinned to
10.0.x in Kpi.Data and Kpi.Copy: SqlClient 6.1 brings a vulnerable 9.0.11. DevExpress.Blazor 25.2 ships a .NET 8 build,
which runs on 10. CMS.Web's KPI pages (the copies' originals) are still .NET 8.

Connection strings live in **user-secrets** (each csproj has a `UserSecretsId` label) or environment variables,
never in committed files: `Kpi.Tools` needs `CmsKpi`, `Cms` (for compare) and `Export` (the KPI_Export server); `Kpi.Push` needs `Source`
(local KPI_Export, Windows sign-in) and `CmsKpi`; `Kpi.Web` needs `CmsKpi` only. **Where the site copies FROM is not a
connection string: it is the CMS data source card in Settings > Integrations** (`Integrations/CmsSource`, provider
`CmsSource` in `Cfg.Integration`: host, port, database, username and the encryption ticks in `SettingsJson`, the password
in `SecretsCipher` like Resend's key; tested before saving). In Azure: the office's public IP and forwarded port,
`KPI_Export`, the grant-pull login, encrypted with the server's own certificate accepted (none is installed; Steve chose
not to ask PCS for one). With no card connected, `ConnectionStrings:KpiExport` stands in (a developer's machine); with
neither, the copy is off. The card's **Try a query** runs as that same login (so it sees only what the copy can) in a
transaction that is always rolled back, 100 rows / 30 s, System Admin only, each query audited (`SourceQueried`). Local values: `Server=.\SQLEXPRESS;Database=<db>;Trusted_Connection=True;TrustServerCertificate=True`.

## The data flow

**A PULL (16 Oct 2026, Steve; it replaced a planned push from the office):** the `KpiExport` views IN THE CMS DATABASE
(CMS_Live on PF-APP1; CMS_Dev locally) → SQL Server Agent on PF-APP1 copies them into `KpiCopy.*` in the separate
`KPI_Export` database every 10 minutes, 07:00-20:00 Mon-Sat (`KpiCopy.spRefresh`, one transaction, stamps
`KpiCopy.Refresh`), and at 00:15 first prepares Bad Debt and Exposure in CMS (`KpiCopy.spNightly`) → the SITE (`KpiPull`, every 2 minutes, through the CMS data source card) reads that one row and, when it is newer than
CMS_KPI's last publish (`Rpt.spDataStatus`), bulk-copies `KpiCopy.*` (+ the snapshots when prepared since the last
nightly publish) into `Stage.*` in `CMS_KPI` → `Stage.spPublish` swaps everything into `Dim`/`Fact` in ONE transaction
(read-committed snapshot, so a report never sees half a load; it refuses an empty `Stage.Lead`) → the site runs only
`Rpt.*`. Figures are at most ~12 minutes behind CMS; the timings can't drift, because only the office has a schedule. A
refresh during a copy is noticed (the row is re-read) and nothing is published until the next try. Every load replaces
the tables in full (25k paid leads).

- **Why a pull with local copies:** the site reaches the office through ONE port-forward rule that admits only its own
  static IP (20.90.27.132, below); its login (`kpi_pull`) can read `KpiCopy.*`, the two snapshots and the version -
  NOTHING in CMS (ownership chaining is off on PF-APP1, so reading the views would have needed rights on CMS_Live's
  tables, as Klipfolio's `Fendra` login has). The copies also make a refresh one consistent moment and keep the long
  read over the internet off CMS_Live.

- **Schemas in CMS_KPI:** `Stage` (landing + `spPublish`), `Ref` (`Setting` report date and load times, `LoadRun`
  log, EF history), `Cfg` (KPI-owned settings: `SalespersonScope`, `PostcodeArea`; later `Integration`), `Dim`, `Fact`
  (Bad Debt / Exposure are last night's results of the desktop's own procedures), `Rpt` (the report procedures,
  ports of CMS's WebKPI ones), `Auth` (the site's own sign-in: Identity's tables as `Auth.Users` / `Roles` / ...,
  and the append-only `Auth.AuditEntry`), `Cfg.Integration` (Resend). Nothing about CMS's users is copied: KPI rights are
  set on the KPI site (Oct 2026, Steve).
- **Roles:** `kpi_site` (EXECUTE `Rpt`; read/write its own `Auth` tables and `Cfg.Integration`, with UPDATE/DELETE on
  `Auth.AuditEntry` DENIED), `kpi_push` (fill `Stage`, run `spPublish` and `spRecordFailure`, nothing else - the SITE's
  identity is in it too, to copy), `kpi_reader` (EXECUTE `Rpt`). Logins/users are added by hand per server. In
  KPI_Export: `kpi_pull` (made by the script; members by `grant-pull`).
- **The copy logs every failure it can:** `Stage.spPublish` logs its own; a copy that fails BEFORE publishing (reading
  CMS, the bulk copy) calls `Stage.spRecordFailure` if it can still reach CMS_KPI. Settings > Data sync shows
  `Ref.LoadRun` (`Rpt.spLoadHistory`) and the overdue flags (`Rpt.spDataStatus`). The site records a failing copy
  once when it starts failing and then hourly (not every 2 minutes); every attempt is in the site's log. A refresh
  that stops in the office shows as Overdue, with the Agent job's history on PF-APP1.
- **"As at" on every report = the business date AND the time of the last copy** (`KpiDataStatus`, Rpt.spDataStatus,
  cached a minute; full view and phone bar), so a reader sees how fresh the figures are; amber with a warning when the
  copy is overdue. A past year's figures (CMS's current year set back) show the date alone. "Refreshed" is only when
  that browser last fetched.
- **Two halves (18 Oct 2026, Steve - the views moved into CMS so a CMS change that breaks one is seen in the CMS repo):**
  - **WHAT leaves CMS = the `KpiExport` schema in the CMS database**, authored in the CMS REPO (migration `mgKpiExport`,
    readable copies + README in `CMS.Models/Scripts/KpiExport/`, a rule in CMS's CLAUDE.md): the 12 `v*` views (the
    only place CMS's tables and columns are named), `fnReportDate`, `spPrepareSnapshots` + `BadDebtSnapshot` /
    `ExposureSnapshot` / `SnapshotRun`, and `Version`. Changed only by a new CMS migration.
  - **HOW it travels = the `KPI_Export` database**, NO migrations (Steve): one rerunnable script,
    `Kpi.Copy/Export/KPI_Export.sql`, applied by `export-apply --cms <db>` (refuses to run until the CMS side is there at
    the expected version). `KpiCopy.Source` names the CMS database; `KpiCopy.spRefresh` rebuilds `KpiCopy.X` from
    `<CMS>.KpiExport.vX` for EVERY view there (+ the two snapshots) in one transaction by three-part name; `spNightly`
    runs `<CMS>.KpiExport.spPrepareSnapshots` first. Names no CMS table or column. Each run drops and recreates its
    objects (and v2's empty view/synonym schemas) but keeps the `kpi_pull` role and its members, and creates the two
    Agent jobs ONLY IF MISSING (an existing job's step is repointed; its schedule survives; not on SQL Express). Simple
    recovery. The Agent jobs run as sa and read CMS; the site's login (`kpi_pull`) reads `KpiCopy` only.
  - **Versions (`Kpi.Copy/Export/ExportVersion.cs`):** `Current` = KPI_Export's `KpiCopy.Version` (3), checked by the copy;
    `Cms` = the CMS side's `KpiExport.Version` (1), checked by the refresh (and the script's `@CmsVersion`). A view
    whose COLUMNS change: a CMS migration raises `KpiExport.Version`, and this repo raises `Cms` + `@CmsVersion` (and
    `KpiCopier`/Stage if a table's shape changes). `export-check` (read only) reports both versions and runs every CMS
    `KpiExport` view. The site copies only the tables listed in `KpiCopier`. Applied by the user, never by Claude.
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
- **Year on Year compares TO THE SAME DATE (19 Oct 2026, Steve - as Klipfolio, unlike CMS.Web's copy):** the current
  month's row, the YTD total and the current year's bottom-line % use the prior year's `TotalToDate` (a column
  `Rpt.spYearOnYear` gained in migration `YearOnYearToDate`: each period up to the report date's day of the year), not
  its whole month / year. Desktop page and phone view both; `compare` ignores that one KPI-only column. CMS.Web's
  Year on Year was deliberately NOT changed (its KPI pages are expected to be retired).
- **Business Analysis by Lender, "vs previous year" (19 Oct 2026, Steve):** a GOLD STAR (`--kpim-star`) = a new lender,
  no business at all the year before; a DASH = business the year before but none by this date (the report year compares
  to the same date - Klipfolio shows a star for both). Each has hover text saying which; the phone view says "New" / "–".
- **The site reads REPORT data only through `Rpt` procedures** (Dapper, `KpiDb`) — never a copied table directly; a new
  list or figure is a new `Rpt` procedure (a migration). The site's OWN tables (`Auth.*`, `Cfg.Integration`) go
  through EF Core (`KpiDbContext`).
- **KPI passwords stay separate from desktop passwords** — never make this database part of the desktop sign-in.

## Sign-in, My Account, Settings (built Oct 2026, as DavidsonHR `C:\GitRepo\DavidsonHR`)

- **ASP.NET Core Identity on EF Core** (`KpiUser` in `Auth.Users`), a COMPULSORY **code texted to the person's mobile**
  (Oct 2026, Steve: "Twilio SMS in all cases" - the authenticator app's QR couldn't be scanned on the phone it was shown
  on), no recovery codes, **invitation only**. **Twilio Verify** (`Integrations/TwilioConnection`, copied from DavidsonHR):
  Twilio makes, texts, expires and checks the code, so the site holds none; the two-factor step is Identity's own
  (`SmsTokenProvider` registered as "Sms", so `TwoFactorSignInAsync`, lockout and "don't ask again for 30 days" work
  unchanged). UK mobiles only (`UkMobile`, stored +447...). Settings > Integrations > Twilio: Account SID, Auth Token
  (encrypted), Verify Service SID, and a TEST NUMBER that receives every code - outside Production nothing is sent without
  one. Until Twilio is connected (`TextCodes`), the code is written to the SITE'S LOG instead: on a developer's machine
  for anyone, on the live site only for the seeded first account before its first use (a new database has no Twilio
  yet). A forgotten password is reset by the emailed link alone - signing in afterwards still needs the texted code.
  `Kpi.Tools reset-sign-in --email <a> [--azure]` puts an account back to the seeded state (password kept) for an admin
  who can't get in. This departs from the CMS hub's "own tables, no Identity" rule deliberately:
  that rule exists so staff and customers share one implementation in CMS.Core (which also builds for the net48
  desktop); this site is standalone .NET 10.
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
  and **a password alone NEVER leads to registering a mobile** — only an emailed link (invitation, reset, re-enrol) or
  My Account's change-mobile form (needs the password inside a signed-in session) opens the ONE-HOUR setup window
  (`AccountSetup`: random value in a `__Host-` cookie, its hash on the user row; `ExpireSetupsTask` closes stale ones and
  clears an unactivated account's password). The page is `/account/mobile`: the number, a code texted to it, the code. The one exception: the account made by `Kpi.Tools seed` (`IsSeeded`), on
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
  reset mobile with "how did you check it was them?", disable, and DELETE - disabled first so every session ends, then removed; the audit keeps their name and history; never yourself or the last System Admin), Integrations (Resend: key encrypted with Data
  Protection, never shown back, blank keeps it; a TEST ADDRESS catches every email, and outside Production nothing is
  sent without one), Data sync, Audit. When an email can't be sent the invitation / reset link is shown to the System
  Admin to pass on.
- **Published links (18 Oct 2026, Steve - to replace the Klipfolio dashboards in CMS's KPI tabs):** Settings > Published
  links makes a link to ONE report, opened at `/view/{key}` without signing in. The key is 32 random bytes; a link is
  found by its SHA-256 (`Cfg.PublishedLink`, migrations `PublishedLinks`, `PublishedLinkUrls`) and the key is kept
  ENCRYPTED (Data Protection), so Settings shows Copy link / Open on every active link (Steve: not sensitive, and locked
  to the allowed addresses; a key ring change makes old URLs unshowable - they still open; remake them). Each link has
  SHOW HEADER (the report's title and As at / Refreshed lines; `.kpi-view-noheader` hides `.kpi-topbar`), changeable
  any time without changing the URL - an open tab follows at its next minute check. `PublishedLinkDoor` (before AccountGate; `/view` is on the gate's open list) checks the key and the
  ALLOWED ADDRESSES (a `Cfg.Integration` row, provider `PublishedLinks`; EMPTY = any address - CMS's users at home
  come from their own address, the VPN carrying only office traffic), counts the open, sets no-store / no-referrer.
  `PublishedView` renders the report page as a plain component (its route, layout and [Authorize] don't apply) inside
  `.kpi-view`, which account.css uses to hide the sidebar, avatar menu and phone view and keep the full layout at any
  width - the copied report pages are UNTOUCHED. The key is re-checked every minute (a revoked link stops an open
  tab). Made / revoked / addresses changed and refused opens (at most hourly per address) are audited. CMS's tab
  control (`ucKlipfolioDashboard`) still runs Klipfolio's sign-in script after every load: it must skip that for a
  row without a password before a tab is switched over (a CMS change, not made yet).
- **The Home Screen app (Oct 2026, Steve):** `manifest.webmanifest` + the head tags in App.razor make the site installable,
  opening full screen with no address bar under the "PF KPIs" icon (`img/apple-touch-icon.png`, `kpi-icon-*`: the PF
  dots with KPI in red - the KPI marks it as this site, the dots may serve another app). `js/appInstall.js` offers it
  on a phone or tablet not already in the app: the avatar menu's **Add to Home Screen**, and a bar across the top ONCE
  after each sign-in (`AccountEndpoints.SignedIn` sets the `kpi-app-tip` cookie, the script spends it); both open the
  same instructions (Safari's steps on iOS, the browser menu's elsewhere, a real Install button when Android offers
  one). The bar and instructions sit in App.razor OUTSIDE the Blazor app. **The app does not share the browser's
  sign-in** - it signs in afresh (password + code), and needs "Keep me signed in" (14 days from last use; the code step's "don't ask again" is 30 days) to survive being closed on an iPhone.
- **A phone that slept comes back quietly:** circuits are kept 30 minutes after a disconnect (`Program.cs`, default 3),
  `js/reconnect.js` reconnects the moment the page is looked at again and reloads by itself when the server no longer
  has the circuit, and Blazor's full-screen "Attempting to reconnect… 1 of 8" is replaced by a small note
  (`#components-reconnect-modal` in App.razor). With `viewport-fit=cover`, account.css keeps the phone frame clear of
  the notch (over kpi.css, which stays CMS.Web's copy).
- **Morning pre-load (16 Oct 2026, Steve: "a bit slow at times"):** cache keys carry the business date, so the morning's
  first copy leaves every report cold and its first viewer waited for the full calculation on the S0 database. `KpiCache`
  remembers each view actually opened (key minus its date + its loader, forgotten after 3 days unused) and `KpiWarmer`
  reloads them, one at a time, when the date moves (checked every 2 min). Empty after a restart. If it is still slow,
  the next step is the database S0 -> S1 (one setting, reversible) - measure first.
- **The pre-load, widened (18 Oct 2026, Steve: Sales Summary / Salesperson Summary "always slow" - 9-15 Rpt.spSalesSummary
  calls a view, ~0.2-0.7 s each on Azure, and the shared cache was empty after every restart).** `KpiWarmer` (one
  singleton, also a hosted service) fills the cache at START-UP, on a NEW BUSINESS DATE, and after every NEW DATASET the
  site's copy publishes (`KpiPull` calls `AfterCopy()` - Steve: "from the sync"): it re-runs every remembered view
  (`KpiCache.RefreshViewsAsync`; same keys, new figures) and makes sure every cell of both pages is cached
  (`SalesSummaryCells` - also the ONE key and loader the desktop panel and the phone card now share; before, a phone
  fetched each figure twice). One run and one query at a time. Bigger fix if first views still bother: one Rpt
  procedure returning a whole page's grid. Caching to be discussed further (Steve).
- **Data Protection key ring** encrypts cookies, emailed links and the Resend key: locally the user profile; **in Azure it
  must be a shared persisted ring (storage + Key Vault)** or every restart signs everyone out and the Resend key becomes
  unreadable. Pin `AllowedHosts` in Azure: emailed links are built from the request's host.

## Azure (LIVE for testing, 16 Oct 2026)

In Performance Finance's subscription ("Azure subscription 1", tenant `5917b69c-...`), UK West, named generically so
other apps can share them. Steve is **Contributor on the whole subscription** (16 Oct 2026, granted through PF's admin
account; before that only on `attachment-prod`/`-dev`). The site and database are in **`attachment-prod`**; the network
pieces in **`rg-pf-apps`** (created 16 Oct). Resources can move between groups later - a public IP keeps its address; a
NAT gateway can't move and is recreated:

- **Static outbound IP (16 Oct 2026, for the pull):** `pip-pf-apps-out` = **20.90.27.132** (Standard, static) on NAT gateway
  `nat-pf-apps`, on subnet `snet-pf-apps-web` (10.20.1.0/26, delegated to App Service) of `vnet-pf-apps` (10.20.0.0/16), all in
  `rg-pf-apps`. `pf-kpi` is VNet-integrated there with ALL application traffic routed through it, so everything the site
  sends out leaves from 20.90.27.132 - the ONE address PF's office firewall admits (port forward requested from PCS
  16 Oct: outside port of their choice -> PF-APP1 192.168.1.9:1433). About GBP 30 a month. `Microsoft.Network` had to be
  registered on the subscription first.

- **SQL server `pf-sql-uk`**: admin = group **PF Database Admins** (Steve, Andy). **Entra-only** (password sign-in was
  on 16-18 Oct for the abandoned push's `kpi-push-office` user; that user was dropped and Entra-only restored 18 Oct).
  Firewall: `pf-apps-nat` (20.90.27.132, the site) and `steve-current` only - the site's 31 shared-address rules were
  deleted 16 Oct, and with only these the site restarted and `/health/db` said ok: the proof that it goes out through
  20.90.27.132. **`CMS_KPI`**: Standard **S1** (S0 until 18 Oct 2026), CMS_Live's collation. Migrated, seeded and
  first filled by hand from CMS_Dev with Kpi.Push's old `--azure`; since 18 Oct only the site's own copy fills it. `Kpi.Tools ... --azure` (secret `ConnectionStrings:CmsKpiAzure`,
  `Authentication=Active Directory Interactive`).
- **App Service plan `plan-pf-apps`** (B1 Linux) + **web app `pf-kpi`**: .NET 10, Always On, WebSockets, HTTPS only, system
  managed identity (app id `61d59e76-...`, a user in `kpi_site` by `grant-site`). App settings: `ASPNETCORE_ENVIRONMENT`
  Production, `AllowedHosts` (`kpi.performancefinance.co.uk;pf-kpi.azurewebsites.net`), `ConnectionStrings__CmsKpi`
  (`Authentication=Active Directory Managed Identity;Encrypt=Strict`), `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (Azure
  ends HTTPS in front of the app; without it the site thinks it is on http and builds http:// links). Container logs to
  the filesystem (`/home/LogFiles/*_default_docker.log`, through Kudu).
- **Domain: `kpi.performancefinance.co.uk`** (live 16 Oct 2026): PF's DNS people pointed it here (CNAME `kpi` →
  `pf-kpi.azurewebsites.net` + TXT `asuid.kpi` = the app's Custom Domain Verification ID); free App Service certificate
  (SNI, renews itself, first one to Apr 2027). A name bound WITHOUT a certificate shows browsers Azure's
  `*.azurewebsites.net` one (`ERR_CERT_COMMON_NAME_INVALID`) - what people saw when PF's DNS change reached them before
  the certificate existed. `kpi.fendra.co.uk` (the testing address) was RETIRED the same day: its binding, certificate
  and `AllowedHosts` entry removed; its 123-reg records (`kpi` CNAME, `asuid.kpi` TXT) are Steve's to delete. Sign-ins,
  "don't ask again" and the Home Screen app belong to each address, so they start afresh on the new one.
- **Deploying** (no pipeline yet): `dotnet publish Kpi.Web -c Release`, zip with FORWARD-SLASH paths (Windows
  PowerShell 5.1's `Compress-Archive` writes backslashes, which Linux unpacks as flat file names), then POST it to
  `https://pf-kpi.scm.azurewebsites.net/api/publish?type=zip&clean=true&restart=false` with a bearer token from
  `az account get-access-token --subscription "Azure subscription 1"`. **`clean=true` empties the site first: never upload
  if the publish failed** - an empty zip took the site down for 10 minutes on 15 Oct. **Stop the app first (`az webapp stop`),
  upload, then `az webapp start`**: uploading over the RUNNING site swaps files under the open process and left a damaged
  DLL behind (16 Oct: `BadImageFormatException: Index not found` on some pages until a clean redeploy). **After every deploy, `https://kpi.performancefinance.co.uk/health/db` must say `ok`**
  (a real query through the site's managed identity - "the login page opens" proved nothing on 16 Oct).
- **Health check and alert (16 Oct 2026):** `/health` (running) and `/health/db` (a real `SELECT 1`, 20 s limit; 503
  `unavailable`, detail in the log) - anonymous, on the AccountGate's open list. App Service's health check pings
  `/health/db`; the metric alert "KPI site cannot reach its database" (HealthCheckStatus < 100 over 5 min) emails
  Steve through action group `ag-pf-kpi-alerts`.
- **Still before go-live:** the Data Protection key ring is the App Service default (`/home`, survives restarts, not
  encrypted at rest) - wrap it with Key Vault (the CMS data source card's saved password depends on it too); the pull
  switched on - see `docs/kpi-pull-runbook.md`. **Done 18 Oct 2026:** `mgKpiExport` on CMS_Live (with CMS publish 1426);
  KPI_Export v3 + its two Agent jobs on PF-APP1 (Steve ran the script in SSMS with `@CmsDb = CMS_Live` rather than
  Kpi.Tools - equivalent); first `spNightly` by hand: 25,002 leads; the site's login `kpi_site_azure` made by an SSMS
  script equivalent to `grant-pull` (password made by SQL Server, kept only in Steve's password manager; also DENY VIEW
  ANY DATABASE) and its limits proved; the push's Azure leftovers removed. **Left:** `grant-push` for the site's identity,
  PCS's port forward, then the CMS data source card. Until then the figures are a developer's hand
  copy from CMS_Dev.
- **The office SQL Server (PF-APP1, found 16 Oct 2026):** SQL Server 2017 Standard, security updates current (support
  ends 12 Oct 2027); ownership chaining off; KPI_Export / CMS_Live / Dashboard owned by Steve's Windows account.
  Klipfolio already reaches it through a port forward of 1433 (Klipfolio's shared addresses) as **`Fendra`, a sysadmin**,
  unencrypted, the login the CMS desktop also uses - a separate fix (own read-only login for Klipfolio, encryption,
  then a new Fendra password with a desktop update) for Steve and PCS, not part of the KPI site.

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
- **NO REPORT MAY LOAD FOR EVER (16 Oct 2026).** The night of the .NET 10 upgrade the live site reached its database
  not once for six hours - no connection, no error logged - while every page still opened. Report loads go through
  `KpiCache`'s per-report gate, SHARED by all users, so the first stuck load held every later viewer on "Loading..."
  until a restart (laptop and phone alike; signing out didn't help), and the reports' database calls are synchronous,
  so a stuck one froze the page's own thread - the menu too. Now: a load runs on the thread pool and gives up after
  ONE minute, waiting for the gate included (`KpiCache.LoadTimeout`, `TimeoutMessage`, which the pages show as
  their load error), releasing the gate; every `KpiDb` query is limited to 50 s off the page's thread; Record
  Performances no longer swallows a failure into endless "Loading"; `KpiDataStatus` shows the date alone when it
  can't ask. Proved with a load that never returns: each viewer gets the message within a minute of arriving, and
  the next viewer loads normally once the database answers. Inside the container .NET 10 DID connect (first token
  13 s, first connection ~10 s, then fast), and the site connected after a restart - what stuck that first start is
  not known, which is why the limits matter more than the cause.
- **"Timeout expired ... obtaining a connection from the pool ... max pool size was reached" (18 Oct 2026) did NOT mean
  the pool was full.** One phone view of Salesperson Summary started ~15 report loads at once (4 salespeople x Year /
  Quarter / Month, plus the phone cards - the hidden desktop view loads all of them), each wanting a NEW connection;
  new connections from App Service are slow (8-32 s) and made largely one after another, so the later ones waited past
  the 15 s Connect Timeout. Idle pooled connections close after a few minutes, so it came back "after time away"; each
  deploy also empties the report cache. Fixed four ways: `Min Pool Size=5;Connect Timeout=60` on the site's
  `ConnectionStrings__CmsKpi` (connections kept ready), `ThreadPool.SetMinThreads(64)` (the copied pages' synchronous
  waits no longer starve the one-core plan), at most 6 report queries at once (`KpiDb`'s semaphore; the rest queue on
  open connections, within the same minute), and CMS_KPI S0 -> **S1**.
- **Windows PowerShell 5.1's `ConvertFrom-Json` passes a JSON ARRAY down the pipe as ONE object**: `... | ConvertFrom-Json |
  Where-Object { $_.name -like "x*" } | ForEach-Object { az ... delete --ids $_.id }` matched the whole list and deleted
  ALL 34 SQL firewall rules (16 Oct; the 3 keepers restored from a saved copy within minutes - firewall changes take a
  few minutes to apply, so the site never noticed). Wrap it: `(... | ConvertFrom-Json) | Where-Object ...`, and list
  what a delete will hit before running it. JMESPath queries with quotes (`[?starts_with(name,'x')]`) don't survive
  PowerShell -> az.cmd either.
- **The App Service clock is UTC: never `ToLocalTime()` / `DateTime.Now` for a time someone reads** - "Refreshed on" did,
  and showed an hour behind "As at" in summer (19 Oct 2026). Show times through `Data/UkTime`.
- **Ctrl+C on `dotnet run` can leave the program running** (a seed waiting at `Password:`): its exe then locks
  `bin/` and every build fails with MSB3027 until it is stopped. Answer the seed's prompts promptly - its Azure sign-in
  expires.
