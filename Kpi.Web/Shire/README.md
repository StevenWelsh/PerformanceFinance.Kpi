# Shire Salesforce (3 Nov 2026)

Three reports in Shire Professional Funding's colours, under **SHIRE - SALESFORCE** in the menu: **Shire Year on Year**,
**Shire Sales Performance** and **Shire Business Analysis**. Only the people ticked for them in Settings > Users see them
(tick-only: System Admin and "All reports" don't include them). Their figures come from Shire's Salesforce and mirror Shire's own Salesforce reports: a sale
is an opportunity at **Closed Won**, counted on its **Close Date**; Amount = Amount Financed, Income = `Income__c`.

## Where everything is (the queries, for review)

| What | File |
|---|---|
| Every Salesforce query (SOQL) the copy runs | `Kpi.Web/Shire/ShireQueries.cs` |
| Every SQL object: tables, the publish, the report procedures, rights | `Kpi.Data/Shire/001_ShireSalesforce.sql` (run by migration `ShireSalesforce`) |
| Business Analysis: four more fields, its two procedures, the Facility grouping | `Kpi.Data/Shire/002_ShireBusinessAnalysis.sql` (migration `ShireBusinessAnalysis`) |
| Signing in, querying, the card's test and Try a query | `Kpi.Web/Shire/ShireSalesforce.cs` |
| The copy (check, copy, publish) | `Kpi.Web/Shire/ShirePull.cs` |
| Shire sync's pause / interval / Copy now | `Kpi.Web/Shire/ShireCopyControl.cs` |
| The reports' data, "As at", refresh signal | `Kpi.Web/Shire/ShireReports.cs` |
| Pages, phone views, the Salesforce card, Shire sync | `Kpi.Web/Components/Shire/` |
| Colours | `Kpi.Web/wwwroot/css/kpi.css`, section "Shire - Salesforce" |

## How the copy works

- **Settings > Integrations > Salesforce (Shire):** address, the External Client App's Consumer Key and Secret (client
  credentials; it runs as `pf.integration@shirepf.co.uk`, read only), tested before saved, encrypted. Try a query runs SOQL.
- **Every 15 minutes** (Settings > Shire sync: 5 to 60, pause, Copy now) one call asks Salesforce how many opportunities
  there are and when any last changed. Only when that moved does it copy the **won deals** (about 5,400) in full into
  `Shire.StageSale`; `Shire.spPublish` swaps them into `Shire.Sale` in one transaction. A deal deleted, re-opened or lost
  simply drops out. Only what the reports use is copied: Id, Close Date, Amount, Income, owner, and Proposed to PF (the
  funder is Performance Finance - Shire keeps one opportunity per funder); for Business Analysis the client company's
  Sector, the deal's Finance Product, Loan Purpose and Funder picklist. No names of companies or deals, no directors,
  addresses, bank details or notes.
- Every copy and failure is listed on Shire sync (`Shire.LoadRun`); a check with nothing new isn't, it just keeps "Last
  check" current. The reports' "As at" is amber when there has been no successful check for 45 minutes in office hours.

## Changing it

An applied SQL file is never edited. Add the next numbered `.sql` with a new migration (copy `20261103170000_ShireBusinessAnalysis.cs`).
