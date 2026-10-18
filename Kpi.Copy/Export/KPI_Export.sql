/*  KPI_Export - the database, beside CMS on the same SQL Server, through which the KPI site collects its figures
    (docs/kpi-pull-runbook.md). It holds COPIES only; what leaves CMS is defined in CMS itself.

    WHAT LEAVES CMS is the KpiExport schema IN THE CMS DATABASE (CMS repo: migration mgKpiExport, readable copies in
    CMS.Models/Scripts/KpiExport/) - the views, fnReportDate and the nightly Bad Debt / Exposure snapshots. It lives
    there so a CMS change that would break it is seen in the CMS repo (Oct 2026, Steve). Apply that migration first.

    THIS DATABASE (the copy side, nothing here names a CMS table or column):
      KpiCopy.X            a copy of <CMS>.KpiExport.vX for every view there, and of the two snapshot tables
      KpiCopy.Refresh      one row: when the copies were refreshed, as at which business date, snapshots prepared when
      KpiCopy.spRefresh    rebuilds every copy in ONE transaction (SQL Server Agent, every 10 minutes)
      KpiCopy.spNightly    runs <CMS>.KpiExport.spPrepareSnapshots, then spRefresh (Agent, 00:15)
      kpi_pull             the KPI site's login reads KpiCopy and nothing else - it has no user in the CMS database
    The jobs run as sa, which reads the CMS database; the site's login never does.

    The CMS database is named on ONE line below (@CmsDb): CMS_Dev on a developer's machine, CMS_Live on the office
    server. Apply it with Kpi.Tools (sets @CmsDb, checks both versions, runs every batch):
        dotnet run --project Kpi.Tools -- export-apply --cms CMS_Live      (or CMS_Dev on a developer's machine)
        dotnet run --project Kpi.Tools -- export-check                     (both versions, and every CMS view runs)
    or in SSMS, after changing @CmsDb by hand. Safe to run again: it rebuilds everything except the database, the
    kpi_pull role and its members, and the Agent jobs' schedules. Run by a person, never by an app.

    VERSIONS: KpiCopy.Version (this script) = ExportVersion.Current; @CmsVersion (what this script expects of the CMS
    side's KpiExport.Version) = ExportVersion.Cms - both in Kpi.Copy/Export/ExportVersion.cs. Raise them together with
    whatever changed; export-apply refuses a script that disagrees with the code, and the refresh refuses a CMS side of
    another version.  */

USE master;
GO
IF DB_ID(N'KPI_Export') IS NULL
    CREATE DATABASE KPI_Export COLLATE Latin1_General_CI_AS;
GO
-- nothing here needs restoring to a point in time (it is all rebuilt from CMS), so the log never needs backing up
ALTER DATABASE KPI_Export SET RECOVERY SIMPLE;
GO
USE KPI_Export;
GO

-- ================================================================================================
-- clear out a previous run: every object (there is no data here but copies), then the schemas version 2 and earlier
-- used for views and synonyms - those objects live in the CMS database now
-- ================================================================================================
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'DROP ' + CASE O.type WHEN 'V' THEN N'VIEW' WHEN 'P' THEN N'PROCEDURE' WHEN 'U' THEN N'TABLE'
                                      WHEN 'SN' THEN N'SYNONYM' ELSE N'FUNCTION' END
             + N' ' + QUOTENAME(SCHEMA_NAME(O.schema_id)) + N'.' + QUOTENAME(O.name) + N';' + CHAR(10)
FROM   sys.objects O
WHERE  O.type IN ('V', 'P', 'U', 'SN', 'FN', 'IF', 'TF')
  AND  O.is_ms_shipped = 0;
EXEC (@sql);

SET @sql = N'';
SELECT @sql += N'DROP SCHEMA ' + QUOTENAME(S.name) + N';' + CHAR(10)
FROM   sys.schemas S
WHERE  S.name IN (N'KpiExport', N'Lead', N'CON', N'CMS', N'OB', N'Reports')
  AND  NOT EXISTS (SELECT 1 FROM sys.objects O WHERE O.schema_id = S.schema_id);
EXEC (@sql);
GO

IF SCHEMA_ID(N'KpiCopy') IS NULL EXEC (N'CREATE SCHEMA KpiCopy');
GO

-- ================================================================================================
-- KpiCopy.Source  (table): which CMS database the copies come from, and the version of its KpiExport schema expected
-- ================================================================================================
CREATE TABLE KpiCopy.Source
(
    CmsDatabase  sysname  NOT NULL,
    CmsVersion   int      NOT NULL
);
GO

DECLARE @CmsDb sysname = N'CMS_Dev';
DECLARE @CmsVersion int = 1;

IF DB_ID(@CmsDb) IS NULL
    THROW 50001, 'The CMS database named in @CmsDb does not exist on this server.', 1;

DECLARE @found int;
DECLARE @q nvarchar(max) = N'SELECT @v = CASE WHEN OBJECT_ID(N''' + QUOTENAME(@CmsDb) + N'.KpiExport.Version'') IS NULL THEN 0 '
                         + N'ELSE (SELECT MAX(Version) FROM ' + QUOTENAME(@CmsDb) + N'.KpiExport.Version) END;';
EXEC sp_executesql @q, N'@v int OUTPUT', @found OUTPUT;
IF @found <> @CmsVersion
BEGIN
    DECLARE @msg nvarchar(400) = CASE WHEN @found = 0
        THEN CONCAT(@CmsDb, N' has no KpiExport schema yet: apply the CMS migration mgKpiExport (Update-Database) first.')
        ELSE CONCAT(@CmsDb, N'''s KpiExport schema is version ', @found, N'; this script expects version ', @CmsVersion,
                    N'. Use the KPI repo''s script that matches it.') END;
    THROW 50002, @msg, 1;
END

INSERT KpiCopy.Source (CmsDatabase, CmsVersion) VALUES (@CmsDb, @CmsVersion);
GO

-- ================================================================================================
-- KpiCopy.Refresh  (table)
-- One row: when the copies were last refreshed, the business date they are 'as at', and when Bad Debt / Exposure were
-- last prepared. The KPI site reads only this every two minutes, and copies only when a time has moved.
-- ================================================================================================
CREATE TABLE KpiCopy.Refresh
(
    RefreshedUtc  datetime2(3)  NOT NULL,
    ReportDate    date          NOT NULL,
    SnapshotsUtc  datetime2(3)  NULL
);
GO

-- ================================================================================================
-- KpiCopy.spRefresh  (procedure)
-- Copies every <CMS>.KpiExport view vX into the table KpiCopy.X, and the two snapshot tables, all in ONE transaction
-- with the refresh row, so the site never sees half a refresh. The tables are rebuilt each time (SELECT INTO), so they
-- always have exactly the views' columns, and a view added in CMS gets a copy without a change here. Refuses a CMS
-- side of another version. Run by the 'KPI_Export - refresh' Agent job (and by Kpi.Push on a developer's machine).
-- ================================================================================================
CREATE PROCEDURE KpiCopy.spRefresh
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Cms sysname, @Expected int;
    SELECT @Cms = QUOTENAME(CmsDatabase), @Expected = CmsVersion FROM KpiCopy.Source;
    IF @Cms IS NULL THROW 50001, 'KPI_Export does not know its CMS database: run Kpi.Tools export-apply.', 1;

    -- the CMS side: its version, when the snapshots were prepared, the business date
    DECLARE @Version int, @Prepared datetime2(3), @ReportDate date;
    DECLARE @q nvarchar(max) =
          N'SELECT @v = MAX(Version) FROM ' + @Cms + N'.KpiExport.Version; '
        + N'SELECT @p = MAX(PreparedUtc) FROM ' + @Cms + N'.KpiExport.SnapshotRun; '
        + N'SET @d = ' + @Cms + N'.KpiExport.fnReportDate();';
    EXEC sp_executesql @q, N'@v int OUTPUT, @p datetime2(3) OUTPUT, @d date OUTPUT',
         @Version OUTPUT, @Prepared OUTPUT, @ReportDate OUTPUT;
    IF ISNULL(@Version, 0) <> @Expected
    BEGIN
        DECLARE @msg nvarchar(400) = CONCAT(@Cms, N'.KpiExport is version ', ISNULL(@Version, 0), N'; KPI_Export expects ',
            @Expected, N'. Nothing was copied: apply the KPI repo''s script that matches it (Kpi.Tools export-apply).');
        THROW 50002, @msg, 1;
    END

    -- every view vX -> KpiCopy.X, and the two snapshot tables under their own names
    DECLARE @names TABLE (Source sysname, Copy sysname);
    SET @q = N'SELECT V.name, STUFF(V.name, 1, 1, N'''') FROM ' + @Cms + N'.sys.views V '
           + N'JOIN ' + @Cms + N'.sys.schemas S ON S.schema_id = V.schema_id '
           + N'WHERE S.name = N''KpiExport'' AND V.name LIKE N''v%'';';
    INSERT @names EXEC (@q);
    INSERT @names VALUES (N'BadDebtSnapshot', N'BadDebtSnapshot'), (N'ExposureSnapshot', N'ExposureSnapshot');

    DECLARE @copy nvarchar(max) = N'';
    SELECT @copy += N'DROP TABLE IF EXISTS KpiCopy.' + QUOTENAME(Copy) + N'; '
                  + N'SELECT * INTO KpiCopy.' + QUOTENAME(Copy) + N' FROM ' + @Cms + N'.KpiExport.' + QUOTENAME(Source) + N';' + CHAR(10)
    FROM   @names;

    BEGIN TRANSACTION;
        -- one refresh at a time: a second one started while this runs gives up at once (not a failure)
        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = N'KpiExportRefresh', @LockMode = N'Exclusive',
                                   @LockOwner = N'Transaction', @LockTimeout = 0;
        IF @lock < 0
        BEGIN
            ROLLBACK TRANSACTION;
            PRINT 'Another refresh was still running, so this one was skipped.';
            RETURN;
        END

        EXEC (@copy);

        UPDATE KpiCopy.Refresh SET RefreshedUtc = SYSUTCDATETIME(), ReportDate = @ReportDate, SnapshotsUtc = @Prepared;
        IF @@ROWCOUNT = 0
            INSERT KpiCopy.Refresh (RefreshedUtc, ReportDate, SnapshotsUtc) VALUES (SYSUTCDATETIME(), @ReportDate, @Prepared);
    COMMIT TRANSACTION;
END
GO

-- ================================================================================================
-- KpiCopy.spNightly  (procedure): Bad Debt and Exposure in CMS (a few minutes), then a refresh. The 'KPI_Export -
-- nightly' Agent job.
-- ================================================================================================
CREATE PROCEDURE KpiCopy.spNightly
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Cms sysname = (SELECT QUOTENAME(CmsDatabase) FROM KpiCopy.Source);
    IF @Cms IS NULL THROW 50001, 'KPI_Export does not know its CMS database: run Kpi.Tools export-apply.', 1;
    DECLARE @q nvarchar(400) = N'EXEC ' + @Cms + N'.KpiExport.spPrepareSnapshots;';
    EXEC (@q);
    EXEC KpiCopy.spRefresh;
END
GO

-- ================================================================================================
-- the version: the KPI site and Kpi.Push copy only from the KPI_Export they were built for (ExportVersion.Current)
-- ================================================================================================
CREATE TABLE KpiCopy.Version (Version int NOT NULL, AppliedUtc datetime2(0) NOT NULL);
INSERT KpiCopy.Version (Version, AppliedUtc) VALUES (3, SYSUTCDATETIME());
GO

-- ================================================================================================
-- kpi_pull: the KPI site's login (Kpi.Tools grant-pull) reads KpiCopy - the copies, the refresh row, the version -
-- and nothing else; it has no user in the CMS database. The role and its members survive a rerun.
-- ================================================================================================
IF DATABASE_PRINCIPAL_ID(N'kpi_pull') IS NULL CREATE ROLE kpi_pull;
GRANT SELECT ON SCHEMA::KpiCopy TO kpi_pull;
GO

-- ================================================================================================
-- the SQL Server Agent jobs (not on SQL Express, which has no Agent: there run EXEC KPI_Export.KpiCopy.spRefresh or
-- spNightly by hand, or let Kpi.Push do it). Created when MISSING, so a schedule changed in SSMS survives running this
-- again; an existing job's step is pointed at this version's procedure.
--   KPI_Export - refresh   every 10 minutes, 07:00-20:00, Monday to Saturday
--   KPI_Export - nightly   00:15 daily: Bad Debt and Exposure (a few minutes), then a refresh
-- Owned by sa (whatever it is called here), so the steps can read the CMS database; no program, no password.
-- ================================================================================================
IF CAST(SERVERPROPERTY('EngineEdition') AS int) = 4
    PRINT 'SQL Express has no SQL Server Agent: no jobs created. Refresh by hand with EXEC KPI_Export.KpiCopy.spRefresh.';
ELSE
BEGIN
    DECLARE @owner sysname = SUSER_SNAME(0x01);

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'KPI_Export - refresh')
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = N'KPI_Export - refresh', @owner_login_name = @owner,
             @description = N'Refreshes KPI_Export''s copies of the CMS database''s KpiExport views for the KPI website, which collects them within two minutes. From the PerformanceFinance.Kpi repo (Kpi.Copy/Export/KPI_Export.sql).';
        EXEC msdb.dbo.sp_add_jobstep @job_name = N'KPI_Export - refresh', @step_name = N'Refresh the copies',
             @subsystem = N'TSQL', @database_name = N'KPI_Export', @command = N'EXEC KpiCopy.spRefresh;';
        EXEC msdb.dbo.sp_add_jobschedule @job_name = N'KPI_Export - refresh', @name = N'Every 10 minutes, 07:00-20:00, Mon-Sat',
             @freq_type = 8, @freq_interval = 126, @freq_recurrence_factor = 1,    -- weekly: Mon+Tue+Wed+Thu+Fri+Sat
             @freq_subday_type = 4, @freq_subday_interval = 10,                  -- every 10 minutes
             @active_start_time = 70000, @active_end_time = 200000;
        EXEC msdb.dbo.sp_add_jobserver @job_name = N'KPI_Export - refresh', @server_name = N'(local)';
        PRINT 'Created the job KPI_Export - refresh.';
    END
    ELSE
    BEGIN
        EXEC msdb.dbo.sp_update_jobstep @job_name = N'KPI_Export - refresh', @step_id = 1, @command = N'EXEC KpiCopy.spRefresh;';
        PRINT 'The job KPI_Export - refresh already exists: its schedule is left as it is.';
    END

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'KPI_Export - nightly')
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = N'KPI_Export - nightly', @owner_login_name = @owner,
             @description = N'Prepares the Bad Debt and Exposure snapshots in the CMS database for the KPI website, then refreshes KPI_Export''s copies. From the PerformanceFinance.Kpi repo (Kpi.Copy/Export/KPI_Export.sql).';
        EXEC msdb.dbo.sp_add_jobstep @job_name = N'KPI_Export - nightly', @step_name = N'Bad Debt and Exposure, then the copies',
             @subsystem = N'TSQL', @database_name = N'KPI_Export', @command = N'EXEC KpiCopy.spNightly;';
        EXEC msdb.dbo.sp_add_jobschedule @job_name = N'KPI_Export - nightly', @name = N'Daily at 00:15',
             @freq_type = 4, @freq_interval = 1, @active_start_time = 1500;
        EXEC msdb.dbo.sp_add_jobserver @job_name = N'KPI_Export - nightly', @server_name = N'(local)';
        PRINT 'Created the job KPI_Export - nightly.';
    END
    ELSE
    BEGIN
        -- version 2 had two steps; one now does both
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps S JOIN msdb.dbo.sysjobs J ON J.job_id = S.job_id
                   WHERE J.name = N'KPI_Export - nightly' AND S.step_id = 2)
            EXEC msdb.dbo.sp_delete_jobstep @job_name = N'KPI_Export - nightly', @step_id = 2;
        EXEC msdb.dbo.sp_update_jobstep @job_name = N'KPI_Export - nightly', @step_id = 1,
             @step_name = N'Bad Debt and Exposure, then the copies', @command = N'EXEC KpiCopy.spNightly;',
             @on_success_action = 1;
        PRINT 'The job KPI_Export - nightly already exists: its schedule is left as it is.';
    END
END
GO
