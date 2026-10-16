# Switching on the KPI copy (the pull)

How the KPI site in Azure gets its figures from CMS_Live, and every step to switch it on. Written 16 Oct 2026.

**How it works:** on PF-APP1, SQL Server Agent refreshes the `KPI_Export` database's copy tables from CMS_Live every
10 minutes (07:00–20:00, Monday to Saturday), and at 00:15 prepares Bad Debt and Exposure first. Every 2 minutes the
site reads one row there saying when the copies were last refreshed, and copies them into its own database only when
that time has moved. The site connects from its own static address, **20.90.27.132**, through a port-forward rule that
admits only that address, using a login that can read the copies and nothing in CMS.

**Nothing in CMS changes.** KPI_Export is its own database; it only reads CMS_Live.

**What you need before starting:**

| What | From |
|---|---|
| The **outside port** PCS chose for the new rule | PCS's reply |
| Remote Desktop access to **PF-APP1**, with a Windows account that is a **sysadmin** on its SQL Server (yours owns the databases there, so it should be) | — |
| The KPI repo on your PC, up to date (`git pull`) | — |

Do the parts in order. Part B's password is shown once and typed into the site in Part C, so do B and C in one sitting.

---

## Part A — On your PC: build Kpi.Tools for the server

1. In a terminal at `C:\GitRepo\PerformanceFinance.Kpi`:
   ```bash
   dotnet publish Kpi.Tools -c Release -r win-x64 --self-contained -o C:\Temp\Kpi.Tools
   ```
   `--self-contained` puts .NET inside the folder, so PF-APP1 needs nothing installed.
2. Copy the folder `C:\Temp\Kpi.Tools` to PF-APP1, e.g. to `C:\Apps\Kpi.Tools` (copy and paste over Remote Desktop
   works).

## Part B — On PF-APP1: create KPI_Export, its jobs and the site's login

1. **Check SQL Server Agent is running.** Open **Services**, find **SQL Server Agent (MSSQLSERVER)**:
   - **Status** must be **Running**, and **Startup Type** **Automatic**.
   - If the name in brackets isn't MSSQLSERVER, the SQL Server is a *named instance*: note the name, and in step 3 use
     `Server=.\<that name>` instead of `Server=.`.
2. **Open PowerShell** (an ordinary window, signed in as yourself) and go to the folder:
   ```powershell
   cd C:\Apps\Kpi.Tools
   ```
3. **Tell Kpi.Tools which SQL Server to use** (this window only; nothing is saved):
   ```powershell
   $env:ConnectionStrings__Export = "Server=.;Database=master;Integrated Security=True;TrustServerCertificate=True"
   ```
4. **Create KPI_Export**, reading CMS_Live:
   ```powershell
   .\Kpi.Tools.exe export-apply --cms CMS_Live
   ```
   You should see:
   ```
   .: applying KPI_Export version 2, reading CMS_Live (28 batches)...
     Created the job KPI_Export - refresh.
     Created the job KPI_Export - nightly.
   Done. Check it with:  Kpi.Tools export-check
   ```
   Safe to run again at any time: it rebuilds KPI_Export's objects and leaves existing jobs (and any schedule you've
   changed) as they are.
5. **Check it:**
   ```powershell
   .\Kpi.Tools.exe export-check
   ```
   It must end with **All good.** If it lists problems, stop and send me the output.
6. **Fill the copies for the first time.** In **SSMS** on PF-APP1:
   1. In Object Explorer, expand **SQL Server Agent** > **Jobs**.
   2. Right-click **KPI_Export - nightly** > **Start Job at Step...** > select step 1 > **Start**. It takes a few
      minutes (Bad Debt and Exposure, then the copies). It must finish with **Success**.
   3. Check the result in a new query window:
      ```sql
      SELECT * FROM KPI_Export.KpiCopy.Refresh;
      ```
      One row: `RefreshedUtc` and `SnapshotsUtc` a moment ago (UTC, so an hour behind the clock in summer),
      `ReportDate` today.
7. **Create the site's login.** Back in PowerShell:
   ```powershell
   .\Kpi.Tools.exe grant-pull --user kpi_site_azure
   ```
   It prints a 40-character password **once**. **Leave this window open** until Part C step 2 is done, then close it.
   (If it's lost, run this step again: a new password replaces the old one.)
8. **Prove the login can't reach CMS.** In SSMS, **Connect** > **Database Engine**, server `.`, **SQL Server
   Authentication**, login `kpi_site_azure`, the password from step 7. Then:
   ```sql
   SELECT * FROM KPI_Export.KpiCopy.Refresh;          -- must WORK
   SELECT TOP (1) * FROM CMS_Live.Lead.Lead;          -- must FAIL (not able to access the database / permission denied)
   ```
   Disconnect that window afterwards.

## Part C — In Azure: let the site copy

0. **The site must be running the version with the copy and the CMS data source card** (deployed after 16 Oct 2026).
   If Settings > Integrations has no **CMS data source** card, deploy first (ask Claude, or CLAUDE.md > Deploying).
1. **Let the site write its copies** into its database. On your PC, at `C:\GitRepo\PerformanceFinance.Kpi`:
   ```bash
   dotnet run --project Kpi.Tools -- grant-push --app pf-kpi --app-id 61d59e76-9728-4eea-930b-856fddbd2a8b --azure
   ```
   Sign in when the browser asks. It ends: `pf-kpi is a user in kpi_push.`
2. **Connect the data source in the site.** Sign in to https://kpi.performancefinance.co.uk as a System Admin, then: avatar menu
   (top right) > **Settings** > **Integrations** > the **CMS data source** card > **Connect data source**. Fill in:

   | Field | Value |
   |---|---|
   | Host | `51.148.118.225` |
   | Port | the port PCS chose |
   | Database | `KPI_Export` |
   | Username | `kpi_site_azure` |
   | Password | the password from Part B step 7 (paste it) |
   | Encrypt the connection | ticked |
   | Accept the server's own certificate | ticked |
   | Copy from this data source | ticked |

   Click **Test & save**. It signs in first and saves only if that works; you should see "Connected in … KPI_Export
   version 2, refreshed …". If it says it couldn't connect, nothing was saved: see **If something's wrong**.
3. **Close the PowerShell window on PF-APP1** (the password is now saved, encrypted, in the site). Optionally, in the
   card's **Try a query** box, run `SELECT * FROM KpiCopy.Refresh` to see the office's refresh times, or
   `SELECT TOP (1) * FROM CMS_Live.Lead.Lead` to see CMS refused.
4. **Wait about 3 minutes**, then check in the site: **Settings > Data sync**. The newest entry should be a
   successful load from a minute or two ago, and every report's **As at** today's date and a recent time. The figures
   are now CMS_Live's.

   If nothing has appeared after 5 minutes, see **If something's wrong** below.

## Part D — Tidy up what the earlier "push" plan left behind

Only once Part C works. In your terminal on your PC:

1. **Remove the database user the push would have used.** Portal > search **CMS_KPI** > open the SQL database >
   **Query editor** > sign in with **Microsoft Entra authentication** > run:
   ```sql
   DROP USER [kpi-push-office];
   ```
2. **Turn password sign-in off again** on the Azure SQL server (back to Microsoft sign-in only). As before, make PF's
   subscription the default first, then set yours back:
   ```bash
   az account set --subscription "Azure subscription 1"
   ```
   ```bash
   az sql server ad-only-auth enable --resource-group attachment-prod --name pf-sql-uk
   ```
   ```bash
   az account set --subscription "Standpoint Systems"
   ```
3. **Remove the office's firewall rule on the Azure SQL server** (only the push needed it):
   ```bash
   az sql server firewall-rule delete --subscription "Azure subscription 1" -g attachment-prod -s pf-sql-uk -n pf-office
   ```

---

## If something's wrong

Start with the card: Settings > Integrations > **CMS data source** shows **Last worked** and **Last problem**, and
**Test connection** tries it there and then. Settings > Data sync shows a failed copy once when it starts and then
hourly; the site's own log has every attempt (category `KpiPull`).

| What you see | Likely cause | What to do |
|---|---|---|
| A network error / timeout connecting to 51.148.118.225 | The port-forward rule isn't in place, uses another port, or doesn't admit 20.90.27.132 | Check the port on the card; ask PCS to check the rule |
| `Login failed for user 'kpi_site_azure'` | Wrong password | Run Part B step 7 again for a new password, then the card's **Change** with it |
| `KPI_Export is at version 1; this copy needs version 2` (or any mismatch) | The script on PF-APP1 is older than the site | Part A (fresh build) and Part B steps 2–5 again |
| Data sync shows nothing new, no errors | The jobs aren't refreshing | SSMS > SQL Server Agent > Jobs > right-click a job > **View History**; check the Agent service is running |
| "Overdue" on the reports in office hours | No successful copy for 30 minutes | Both of the above |

## Later

- **Change the refresh interval:** SSMS > SQL Server Agent > Jobs > **KPI_Export - refresh** > Properties >
  **Schedules** > Edit. Re-running `export-apply` won't undo it. (If it's more than 30 minutes, the site's "Overdue"
  warning would need raising too.)
- **New password for the site's login:** Part B step 7 on PF-APP1, then the card's **Change**, paste it, **Test & save**.
- **Pause the copy:** the card's **Change** > untick **Copy from this data source** > **Test & save**.
- **After a change to the KPI_Export script** (a new version): Part A, then Part B steps 2–5, then deploy the site.
