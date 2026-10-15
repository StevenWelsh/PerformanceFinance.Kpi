using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// The KPI database, CMS_KPI: schemas, the copied tables, the staging tables and the publish procedure, the
    /// report procedures (ports of CMS's WebKPI ones) and the database roles. Generated from the KPI database plan;
    /// sign-in (ASP.NET Identity) follows in its own migration.
    /// </summary>
    public partial class KpiSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // collation guard
            migrationBuilder.Sql("""
IF CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) <> N'Latin1_General_CI_AS'
    THROW 50001, 'CMS_KPI must use the collation Latin1_General_CI_AS, the same as CMS_Live. Recreate the database with it.', 1;
""");

            // readers see the last finished load, never a half-done one
            migrationBuilder.Sql("""
IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()) = 0
    EXEC(N'ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE');
""", suppressTransaction: true);

            // report procedures of several statements read as at one moment
            migrationBuilder.Sql("""
IF (SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()) = 0
    EXEC(N'ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON');
""", suppressTransaction: true);

            // schema Ref: settings and the load log
            migrationBuilder.Sql("""
IF SCHEMA_ID(N'Ref') IS NULL EXEC(N'CREATE SCHEMA Ref');
""");

            // schema Cfg: KPI-owned configuration
            migrationBuilder.Sql("""
IF SCHEMA_ID(N'Cfg') IS NULL EXEC(N'CREATE SCHEMA Cfg');
""");

            // schema Stage: where the push lands, and the procedure that publishes it
            migrationBuilder.Sql("""
IF SCHEMA_ID(N'Stage') IS NULL EXEC(N'CREATE SCHEMA Stage');
""");

            // schema Dim: copied reference data
            migrationBuilder.Sql("""
IF SCHEMA_ID(N'Dim') IS NULL EXEC(N'CREATE SCHEMA Dim');
""");

            // schema Fact: copied figures
            migrationBuilder.Sql("""
IF SCHEMA_ID(N'Fact') IS NULL EXEC(N'CREATE SCHEMA Fact');
""");

            // schema Rpt: the report procedures: all the site may run
            migrationBuilder.Sql("""
IF SCHEMA_ID(N'Rpt') IS NULL EXEC(N'CREATE SCHEMA Rpt');
""");

            // schema Auth: sign-in, and each person's CMS rights
            migrationBuilder.Sql("""
IF SCHEMA_ID(N'Auth') IS NULL EXEC(N'CREATE SCHEMA Auth');
""");

            // Ref.Setting
            migrationBuilder.Sql("""
CREATE TABLE Ref.Setting
(
    SettingId            tinyint       NOT NULL  DEFAULT 1,
    ReportDate           date          NOT NULL,
    LastFrequentLoadUtc  datetime2(0)  NULL,
    LastNightlyLoadUtc   datetime2(0)  NULL,
    CONSTRAINT PK_Setting PRIMARY KEY (SettingId)
);

ALTER TABLE Ref.Setting ADD CONSTRAINT CK_Setting_OneRow CHECK (SettingId = 1);

INSERT Ref.Setting (SettingId, ReportDate) VALUES (1, CAST(GETDATE() AS date));
""");

            // Ref.LoadRun
            migrationBuilder.Sql("""
CREATE TABLE Ref.LoadRun
(
    LoadRunId    int IDENTITY(1,1)  NOT NULL,
    Scope        varchar(10)        NOT NULL,
    StartedUtc   datetime2(0)       NOT NULL,
    FinishedUtc  datetime2(0)       NULL,
    Succeeded    bit                NULL,
    Error        nvarchar(2000)     NULL,
    CONSTRAINT PK_LoadRun PRIMARY KEY (LoadRunId)
);

CREATE INDEX IX_LoadRun_StartedUtc ON Ref.LoadRun (StartedUtc DESC);
""");

            // Cfg.SalespersonScope
            migrationBuilder.Sql("""
CREATE TABLE Cfg.SalespersonScope
(
    SalesId         int            NOT NULL,
    FromYear        int            NOT NULL,
    ToYear          int            NULL,
    InLeague        bit            NOT NULL,
    InProductivity  bit            NOT NULL,
    Note            nvarchar(200)  NULL,
    CONSTRAINT PK_SalespersonScope PRIMARY KEY (SalesId, FromYear)
);

INSERT Cfg.SalespersonScope (SalesId, FromYear, ToYear, InLeague, InProductivity, Note) VALUES
    (74, 2000, NULL, 1, 1, N'Susan Marshall'),
    (68, 2000, NULL, 1, 1, N'Pete George'),
    (73, 2000, NULL, 1, 1, N'Ryan O''Dwyer'),
    (82, 2000, NULL, 1, 1, N'Stuart Burn'),
    (83, 2000, 2025, 1, 0, N'Andrew Stevenson: in the league up to 2025, left in 2026'),
    (72, 2024, NULL, 1, 0, N'Own Book: in the league from 2024, not a person');
""");

            // Cfg.PostcodeArea
            migrationBuilder.Sql("""
CREATE TABLE Cfg.PostcodeArea
(
    AreaCode  varchar(2)     NOT NULL,
    District  nvarchar(100)  NOT NULL,
    Region    nvarchar(50)   NOT NULL,
    CONSTRAINT PK_PostcodeArea PRIMARY KEY (AreaCode)
);

INSERT Cfg.PostcodeArea (AreaCode, District, Region) VALUES
    ('AB', N'Aberdeen', N'Scotland'),
    ('AL', N'St. Albans', N'East of England'),
    ('B', N'Birmingham', N'West Midlands'),
    ('BA', N'Bath', N'South West'),
    ('BB', N'Blackburn', N'North West'),
    ('BD', N'Bradford', N'Yorkshire and The Humber'),
    ('BH', N'Bournemouth', N'South West'),
    ('BL', N'Bolton', N'North West'),
    ('BN', N'Brighton', N'South East'),
    ('BR', N'Bromley', N'London'),
    ('BS', N'Bristol', N'South West'),
    ('BT', N'Belfast', N'Northern Ireland'),
    ('CA', N'Carlisle', N'North West'),
    ('CB', N'Cambridge', N'East of England'),
    ('CF', N'Cardiff', N'Wales'),
    ('CH', N'Chester', N'North West'),
    ('CM', N'Chelmsford', N'East of England'),
    ('CO', N'Colchester', N'East of England'),
    ('CR', N'Croydon', N'London'),
    ('CT', N'Canterbury', N'South East'),
    ('CV', N'Coventry', N'West Midlands'),
    ('CW', N'Crewe', N'North West'),
    ('DA', N'Dartford', N'South East'),
    ('DD', N'Dundee', N'Scotland'),
    ('DE', N'Derby', N'East Midlands'),
    ('DG', N'Dumfries', N'Scotland'),
    ('DH', N'Durham', N'North East'),
    ('DL', N'Darlington', N'North East'),
    ('DN', N'Doncaster', N'Yorkshire and The Humber'),
    ('DT', N'Dorchester', N'South West'),
    ('DY', N'Dudley', N'West Midlands'),
    ('E', N'London', N'London'),
    ('EC', N'London', N'London'),
    ('EH', N'Edinburgh', N'Scotland'),
    ('EN', N'Enfield', N'London'),
    ('EX', N'Exeter', N'South West'),
    ('FK', N'Falkirk', N'Scotland'),
    ('FY', N'Blackpool', N'North West'),
    ('G', N'Glasgow', N'Scotland'),
    ('GL', N'Gloucester', N'South West'),
    ('GU', N'Guildford', N'South East'),
    ('GY', N'Guernsey', N'Islands'),
    ('HA', N'Harrow', N'London'),
    ('HD', N'Huddersfield', N'Yorkshire and The Humber'),
    ('HG', N'Harrogate', N'Yorkshire and The Humber'),
    ('HP', N'Hemel Hempstead', N'East of England'),
    ('HR', N'Hereford', N'West Midlands'),
    ('HS', N'Hebrides', N'Scotland'),
    ('HU', N'Hull', N'Yorkshire and The Humber'),
    ('HX', N'Halifax', N'Yorkshire and The Humber'),
    ('IG', N'Ilford', N'London'),
    ('IM', N'Isle of Man', N'Islands'),
    ('IP', N'Ipswich', N'East of England'),
    ('IV', N'Inverness', N'Scotland'),
    ('JE', N'Jersey', N'Islands'),
    ('KA', N'Kilmarnock', N'Scotland'),
    ('KT', N'Kingston upon Thames', N'London'),
    ('KW', N'Kirkwall', N'Scotland'),
    ('KY', N'Kirkcaldy', N'Scotland'),
    ('L', N'Liverpool', N'North West'),
    ('LA', N'Lancaster', N'North West'),
    ('LD', N'Llandrindod Wells', N'Wales'),
    ('LE', N'Leicester', N'East Midlands'),
    ('LL', N'Llandudno', N'Wales'),
    ('LN', N'Lincoln', N'East Midlands'),
    ('LS', N'Leeds', N'Yorkshire and The Humber'),
    ('LU', N'Luton', N'East of England'),
    ('M', N'Manchester', N'North West'),
    ('ME', N'Rochester', N'South East'),
    ('MK', N'Milton Keynes', N'South East'),
    ('ML', N'Motherwell', N'Scotland'),
    ('N', N'London', N'London'),
    ('NE', N'Newcastle upon Tyne', N'North East'),
    ('NG', N'Nottingham', N'East Midlands'),
    ('NN', N'Northampton', N'East Midlands'),
    ('NP', N'Newport', N'Wales'),
    ('NR', N'Norwich', N'East of England'),
    ('NW', N'London', N'London'),
    ('OL', N'Oldham', N'North West'),
    ('OX', N'Oxford', N'South East'),
    ('PA', N'Paisley', N'Scotland'),
    ('PE', N'Peterborough', N'East of England'),
    ('PH', N'Perth', N'Scotland'),
    ('PL', N'Plymouth', N'South West'),
    ('PO', N'Portsmouth', N'South East'),
    ('PR', N'Preston', N'North West'),
    ('RG', N'Reading', N'South East'),
    ('RH', N'Redhill', N'South East'),
    ('RM', N'Romford', N'London'),
    ('S', N'Sheffield', N'Yorkshire and The Humber'),
    ('SA', N'Swansea', N'Wales'),
    ('SE', N'London', N'London'),
    ('SG', N'Stevenage', N'East of England'),
    ('SK', N'Stockport', N'North West'),
    ('SL', N'Slough', N'South East'),
    ('SM', N'Sutton', N'London'),
    ('SN', N'Swindon', N'South West'),
    ('SO', N'Southampton', N'South East'),
    ('SP', N'Salisbury', N'South West'),
    ('SR', N'Sunderland', N'North East'),
    ('SS', N'Southend-on-Sea', N'East of England'),
    ('ST', N'Stoke-on-Trent', N'West Midlands'),
    ('SW', N'London', N'London'),
    ('SY', N'Shrewsbury', N'West Midlands'),
    ('TA', N'Taunton', N'South West'),
    ('TD', N'Galashiels', N'Scotland'),
    ('TF', N'Telford', N'West Midlands'),
    ('TN', N'Tunbridge Wells', N'South East'),
    ('TQ', N'Torquay', N'South West'),
    ('TR', N'Truro', N'South West'),
    ('TS', N'Middlesbrough', N'North East'),
    ('TW', N'Twickenham', N'London'),
    ('UB', N'Uxbridge', N'London'),
    ('W', N'London', N'London'),
    ('WA', N'Warrington', N'North West'),
    ('WC', N'London', N'London'),
    ('WD', N'Watford', N'East of England'),
    ('WF', N'Wakefield', N'Yorkshire and The Humber'),
    ('WN', N'Wigan', N'North West'),
    ('WR', N'Worcester', N'West Midlands'),
    ('WS', N'Walsall', N'West Midlands'),
    ('WV', N'Wolverhampton', N'West Midlands'),
    ('YO', N'York', N'Yorkshire and The Humber'),
    ('ZE', N'Shetland', N'Scotland');
""");

            // Dim.SalesOffice
            migrationBuilder.Sql("""
CREATE TABLE Dim.SalesOffice
(
    SalesOfficeId  int            NOT NULL,
    OfficeName     nvarchar(100)  NOT NULL,
    OrderInt       int            NULL,
    IsActive       bit            NOT NULL,
    CONSTRAINT PK_SalesOffice PRIMARY KEY (SalesOfficeId)
);
""");

            // Dim.Salesperson
            migrationBuilder.Sql("""
CREATE TABLE Dim.Salesperson
(
    SalesId        int            NOT NULL,
    Salesperson    nvarchar(100)  NULL,
    SalesCode      nvarchar(10)   NULL,
    DivisionId     int            NULL,
    SalesOfficeId  int            NULL,
    IsActive       bit            NOT NULL,
    CONSTRAINT PK_Salesperson PRIMARY KEY (SalesId)
);

CREATE INDEX IX_Salesperson_Office ON Dim.Salesperson (SalesOfficeId);
""");

            // Dim.Period
            migrationBuilder.Sql("""
CREATE TABLE Dim.Period
(
    PeriodId         int           NOT NULL,
    PeriodNumber     int           NOT NULL,
    PeriodName       nvarchar(50)  NULL,
    PeriodStartDate  date          NOT NULL,
    PeriodEndDate    date          NOT NULL,
    QuarterId        int           NOT NULL,
    QuarterNumber    int           NULL,
    QuarterName      nvarchar(50)  NULL,
    YearId           int           NOT NULL,
    YearNumber       int           NOT NULL,
    YearName         nvarchar(50)  NULL,
    CONSTRAINT PK_Period PRIMARY KEY (PeriodId)
);

CREATE INDEX IX_Period_Dates ON Dim.Period (PeriodStartDate, PeriodEndDate);
""");

            // Dim.WorkingDay
            migrationBuilder.Sql("""
CREATE TABLE Dim.WorkingDay
(
    WorkingDay  date  NOT NULL,
    CONSTRAINT PK_WorkingDay PRIMARY KEY (WorkingDay)
);
""");

            // Dim.SalesTarget
            migrationBuilder.Sql("""
CREATE TABLE Dim.SalesTarget
(
    SalesId    int            NOT NULL,
    Year       int            NOT NULL,
    AFTarget   decimal(18,2)  NULL,
    TFITarget  decimal(18,2)  NULL,
    CONSTRAINT PK_SalesTarget PRIMARY KEY (SalesId, [Year])
);
""");

            // Dim.FundLender
            migrationBuilder.Sql("""
CREATE TABLE Dim.FundLender
(
    FundLenderId    int            NOT NULL,
    FundLenderName  nvarchar(200)  NULL,
    CONSTRAINT PK_FundLender PRIMARY KEY (FundLenderId)
);
""");

            // Dim.Fund
            migrationBuilder.Sql("""
CREATE TABLE Dim.Fund
(
    FundId        int            NOT NULL,
    FundName      nvarchar(200)  NULL,
    FundLenderId  int            NULL,
    CONSTRAINT PK_Fund PRIMARY KEY (FundId)
);
""");

            // Fact.Lead
            migrationBuilder.Sql("""
CREATE TABLE Fact.Lead
(
    LeadId         int            NOT NULL,
    dtPayReceived  date           NOT NULL,
    IsUnwound      bit            NOT NULL,
    AF             decimal(18,2)  NULL,
    GFI            decimal(18,2)  NULL,
    TFI            decimal(18,2)  NULL,
    SalesII        decimal(18,2)  NOT NULL,
    DivisionId     int            NULL,
    Division       nvarchar(100)  NULL,
    SalesId        int            NULL,
    Salesperson    nvarchar(100)  NULL,
    SalesCode      nvarchar(10)   NULL,
    SalesOfficeId  int            NULL,
    LenderName     nvarchar(200)  NULL,
    Profession     nvarchar(100)  NULL,
    Facility       nvarchar(100)  NULL,
    Arrangement    nvarchar(100)  NULL,
    IsOB           bit            NULL,
    IsOptical      bit            NOT NULL,
    IsNewCustomer  bit            NOT NULL,
    IRR            decimal(18,4)  NULL,
    IRRInclFees    decimal(18,4)  NULL,
    Term           int            NULL,
    MinIRR         decimal(18,4)  NULL,
    PeriodId       int            NULL,
    PostcodeArea   varchar(2)     NULL,
    CONSTRAINT PK_Lead PRIMARY KEY (LeadId)
);

CREATE INDEX IX_Lead_Paid     ON Fact.Lead (dtPayReceived) INCLUDE (AF, TFI, GFI, SalesII, SalesId, DivisionId, SalesOfficeId, IsOB, IsUnwound);
CREATE INDEX IX_Lead_Salesman ON Fact.Lead (SalesId, dtPayReceived);
""");

            // Fact.LeadPipeline
            migrationBuilder.Sql("""
CREATE TABLE Fact.LeadPipeline
(
    LeadId                      int            NOT NULL,
    SalesId                     int            NULL,
    dtLead                      date           NULL,
    LeadStatus                  nvarchar(50)   NULL,
    AF                          decimal(18,2)  NULL,
    GFI                         decimal(18,2)  NULL,
    dtAppAccept                 date           NULL,
    dtActivation                date           NULL,
    IntroducerId                int            NULL,
    IntroducerManagedByHQ       bit            NULL,
    IntroducerAccountManagerId  int            NULL,
    CONSTRAINT PK_LeadPipeline PRIMARY KEY (LeadId)
);

CREATE INDEX IX_LeadPipeline_Sales ON Fact.LeadPipeline (SalesId, dtLead);
""");

            // Fact.SupplierYear
            migrationBuilder.Sql("""
CREATE TABLE Fact.SupplierYear
(
    Year              int            NOT NULL,
    IntroducerId      int            NOT NULL,
    Supplier          nvarchar(200)  NULL,
    Profession        nvarchar(100)  NOT NULL,
    AccountManagerId  int            NULL,
    ManagedByHQ       bit            NOT NULL,
    AmountFinanced    decimal(18,2)  NULL,
    CONSTRAINT PK_SupplierYear PRIMARY KEY ([Year], IntroducerId)
);
""");

            // Fact.BookPosition
            migrationBuilder.Sql("""
CREATE TABLE Fact.BookPosition
(
    LeadId            int            NOT NULL,
    IsOB              bit            NOT NULL,
    RemainingCapital  decimal(18,2)  NULL,
    CustomerKey       varchar(21)    NOT NULL,
    PostcodeArea      varchar(2)     NULL,
    CONSTRAINT PK_BookPosition PRIMARY KEY (LeadId)
);
""");

            // Fact.FundingLine
            migrationBuilder.Sql("""
CREATE TABLE Fact.FundingLine
(
    FundingLineId  int IDENTITY(1,1)  NOT NULL,
    Source         varchar(5)         NOT NULL,
    SourceId       int                NOT NULL,
    LeadId         int                NULL,
    EventDate      date               NOT NULL,
    DivisionId     int                NULL,
    Amount         decimal(18,2)      NULL,
    Term           int                NOT NULL,
    Rate           decimal(18,6)      NOT NULL,
    IsBulkMove     bit                NOT NULL,
    CONSTRAINT PK_FundingLine PRIMARY KEY (FundingLineId)
);

CREATE INDEX IX_FundingLine_Date ON Fact.FundingLine (EventDate) INCLUDE (Source, DivisionId, Amount, Term, Rate, IsBulkMove);
""");

            // Fact.BadDebtYear
            migrationBuilder.Sql("""
CREATE TABLE Fact.BadDebtYear
(
    IsAutoApproved       tinyint        NOT NULL,
    BadDebtYear          int            NOT NULL,
    Id                   int            NOT NULL,
    YearStartDate        date           NOT NULL,
    YearEndDate          date           NOT NULL,
    AmountFinanced       decimal(19,2)  NOT NULL,
    RemainingCapital     decimal(19,2)  NOT NULL,
    ActiveLeadCount      int            NOT NULL,
    WrittenOffAmount     decimal(19,2)  NOT NULL,
    WrittenOffLeadCount  int            NOT NULL,
    RecoveredAmount      decimal(19,2)  NOT NULL,
    BadDebtCosts         decimal(19,2)  NOT NULL,
    TerminatedLeadCount  int            NOT NULL,
    ArrearsLeadCount     int            NOT NULL,
    ArrearsCompanyCount  int            NOT NULL,
    CONSTRAINT PK_BadDebtYear PRIMARY KEY (IsAutoApproved, BadDebtYear)
);
""");

            // Fact.Exposure
            migrationBuilder.Sql("""
CREATE TABLE Fact.Exposure
(
    GroupingKey              tinyint        NOT NULL,
    FundLenderId             int            NOT NULL,
    FundId                   int            NOT NULL,
    RowNumber                int            NOT NULL,
    Id                       int            NOT NULL,
    Name                     nvarchar(200)  NULL,
    LeadCount                int            NULL,
    RemainingCapital         decimal(18,2)  NULL,
    TotalRemainingCapital    decimal(18,2)  NULL,
    PercentRemainingCapital  decimal(18,6)  NULL,
    CustomerType             int            NULL,
    CONSTRAINT PK_Exposure PRIMARY KEY (GroupingKey, FundLenderId, FundId, RowNumber)
);
""");

            // Auth.UserAccess
            migrationBuilder.Sql("""
CREATE TABLE Auth.UserAccess
(
    UserId                int            NOT NULL,
    UserName              nvarchar(20)   NOT NULL,
    FullName              nvarchar(40)   NOT NULL,
    Email                 nvarchar(256)  NULL,
    SalesOfficeId         int            NULL,
    IsRestrictedToOffice  bit            NOT NULL,
    AccountLocked         bit            NOT NULL,
    CanViewKpis           bit            NOT NULL,
    CanViewBadDebt        bit            NOT NULL,
    CanViewExposure       bit            NOT NULL,
    CONSTRAINT PK_UserAccess PRIMARY KEY (UserId)
);
""");

            // Stage.* staging tables: the rows as they arrive, converted by the publish
            migrationBuilder.Sql("""
CREATE TABLE Stage.SalesOffice
(
    SalesOfficeId  int            NULL,
    OfficeName     nvarchar(max)  NULL,
    OrderInt       int            NULL,
    IsActive       bit            NULL
);

CREATE TABLE Stage.Salesperson
(
    SalesId        int            NULL,
    Salesperson    nvarchar(max)  NULL,
    SalesCode      nvarchar(max)  NULL,
    DivisionId     int            NULL,
    SalesOfficeId  int            NULL,
    IsActive       bit            NULL
);

CREATE TABLE Stage.Period
(
    PeriodId         int            NULL,
    PeriodNumber     int            NULL,
    PeriodName       nvarchar(max)  NULL,
    PeriodStartDate  date           NULL,
    PeriodEndDate    date           NULL,
    QuarterId        int            NULL,
    QuarterNumber    int            NULL,
    QuarterName      nvarchar(max)  NULL,
    YearId           int            NULL,
    YearNumber       int            NULL,
    YearName         nvarchar(max)  NULL
);

CREATE TABLE Stage.WorkingDay
(
    WorkingDay  date  NULL
);

CREATE TABLE Stage.SalesTarget
(
    SalesId    int             NULL,
    Year       int             NULL,
    AFTarget   decimal(38,12)  NULL,
    TFITarget  decimal(38,12)  NULL
);

CREATE TABLE Stage.FundLender
(
    FundLenderId    int            NULL,
    FundLenderName  nvarchar(max)  NULL
);

CREATE TABLE Stage.Fund
(
    FundId        int            NULL,
    FundName      nvarchar(max)  NULL,
    FundLenderId  int            NULL
);

CREATE TABLE Stage.Lead
(
    LeadId         int             NULL,
    dtPayReceived  date            NULL,
    IsUnwound      bit             NULL,
    AF             decimal(38,12)  NULL,
    GFI            decimal(38,12)  NULL,
    TFI            decimal(38,12)  NULL,
    SalesII        decimal(38,12)  NULL,
    DivisionId     int             NULL,
    Division       nvarchar(max)   NULL,
    SalesId        int             NULL,
    Salesperson    nvarchar(max)   NULL,
    SalesCode      nvarchar(max)   NULL,
    SalesOfficeId  int             NULL,
    LenderName     nvarchar(max)   NULL,
    Profession     nvarchar(max)   NULL,
    Facility       nvarchar(max)   NULL,
    Arrangement    nvarchar(max)   NULL,
    IsOB           bit             NULL,
    IsOptical      bit             NULL,
    IsNewCustomer  bit             NULL,
    IRR            decimal(38,12)  NULL,
    IRRInclFees    decimal(38,12)  NULL,
    Term           int             NULL,
    MinIRR         decimal(38,12)  NULL,
    PeriodId       int             NULL,
    PostcodeArea   nvarchar(max)   NULL
);

CREATE TABLE Stage.LeadPipeline
(
    LeadId                      int             NULL,
    SalesId                     int             NULL,
    dtLead                      date            NULL,
    LeadStatus                  nvarchar(max)   NULL,
    AF                          decimal(38,12)  NULL,
    GFI                         decimal(38,12)  NULL,
    dtAppAccept                 date            NULL,
    dtActivation                date            NULL,
    IntroducerId                int             NULL,
    IntroducerManagedByHQ       bit             NULL,
    IntroducerAccountManagerId  int             NULL
);

CREATE TABLE Stage.SupplierYear
(
    Year              int             NULL,
    IntroducerId      int             NULL,
    Supplier          nvarchar(max)   NULL,
    Profession        nvarchar(max)   NULL,
    AccountManagerId  int             NULL,
    ManagedByHQ       bit             NULL,
    AmountFinanced    decimal(38,12)  NULL
);

CREATE TABLE Stage.BookPosition
(
    LeadId            int             NULL,
    IsOB              bit             NULL,
    RemainingCapital  decimal(38,12)  NULL,
    CustomerKey       nvarchar(max)   NULL,
    PostcodeArea      nvarchar(max)   NULL
);

CREATE TABLE Stage.FundingLine
(
    Source      nvarchar(max)   NULL,
    SourceId    int             NULL,
    LeadId      int             NULL,
    EventDate   date            NULL,
    DivisionId  int             NULL,
    Amount      decimal(38,12)  NULL,
    Term        int             NULL,
    Rate        decimal(38,12)  NULL,
    IsBulkMove  bit             NULL
);

CREATE TABLE Stage.UserAccess
(
    UserId                int            NULL,
    UserName              nvarchar(max)  NULL,
    FullName              nvarchar(max)  NULL,
    Email                 nvarchar(max)  NULL,
    SalesOfficeId         int            NULL,
    IsRestrictedToOffice  bit            NULL,
    AccountLocked         bit            NULL,
    CanViewKpis           bit            NULL,
    CanViewBadDebt        bit            NULL,
    CanViewExposure       bit            NULL
);

CREATE TABLE Stage.BadDebtYear
(
    IsAutoApproved       tinyint         NULL,
    BadDebtYear          int             NULL,
    Id                   int             NULL,
    YearStartDate        date            NULL,
    YearEndDate          date            NULL,
    AmountFinanced       decimal(38,12)  NULL,
    RemainingCapital     decimal(38,12)  NULL,
    ActiveLeadCount      int             NULL,
    WrittenOffAmount     decimal(38,12)  NULL,
    WrittenOffLeadCount  int             NULL,
    RecoveredAmount      decimal(38,12)  NULL,
    BadDebtCosts         decimal(38,12)  NULL,
    TerminatedLeadCount  int             NULL,
    ArrearsLeadCount     int             NULL,
    ArrearsCompanyCount  int             NULL
);

CREATE TABLE Stage.Exposure
(
    GroupingKey              tinyint         NULL,
    FundLenderId             int             NULL,
    FundId                   int             NULL,
    RowNumber                int             NULL,
    Id                       int             NULL,
    Name                     nvarchar(max)   NULL,
    LeadCount                int             NULL,
    RemainingCapital         decimal(38,12)  NULL,
    TotalRemainingCapital    decimal(38,12)  NULL,
    PercentRemainingCapital  decimal(38,12)  NULL,
    CustomerType             int             NULL
);
""");

            // Stage.spPublish
            migrationBuilder.Sql("""
CREATE PROCEDURE Stage.spPublish
(
    @Scope      varchar(10),
    @ReportDate date
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunId int;
    INSERT Ref.LoadRun (Scope, StartedUtc) VALUES (@Scope, SYSUTCDATETIME());
    SET @RunId = SCOPE_IDENTITY();

    BEGIN TRY
        IF NOT EXISTS (SELECT 1 FROM Stage.Lead)
            THROW 50002, 'Stage.Lead is empty: not publishing (the site keeps the last good figures).', 1;

        BEGIN TRANSACTION;
        DELETE Dim.SalesOffice;
        INSERT Dim.SalesOffice (SalesOfficeId, OfficeName, OrderInt, IsActive)
        SELECT SalesOfficeId, OfficeName, OrderInt, IsActive FROM Stage.SalesOffice;
        DELETE Dim.Salesperson;
        INSERT Dim.Salesperson (SalesId, Salesperson, SalesCode, DivisionId, SalesOfficeId, IsActive)
        SELECT SalesId, Salesperson, SalesCode, DivisionId, SalesOfficeId, IsActive FROM Stage.Salesperson;
        DELETE Dim.Period;
        INSERT Dim.Period (PeriodId, PeriodNumber, PeriodName, PeriodStartDate, PeriodEndDate, QuarterId, QuarterNumber, QuarterName, YearId, YearNumber, YearName)
        SELECT PeriodId, PeriodNumber, PeriodName, PeriodStartDate, PeriodEndDate, QuarterId, QuarterNumber, QuarterName, YearId, YearNumber, YearName FROM Stage.Period;
        DELETE Dim.WorkingDay;
        INSERT Dim.WorkingDay (WorkingDay)
        SELECT WorkingDay FROM Stage.WorkingDay;
        DELETE Dim.SalesTarget;
        INSERT Dim.SalesTarget (SalesId, Year, AFTarget, TFITarget)
        SELECT SalesId, Year, AFTarget, TFITarget FROM Stage.SalesTarget;
        DELETE Dim.FundLender;
        INSERT Dim.FundLender (FundLenderId, FundLenderName)
        SELECT FundLenderId, FundLenderName FROM Stage.FundLender;
        DELETE Dim.Fund;
        INSERT Dim.Fund (FundId, FundName, FundLenderId)
        SELECT FundId, FundName, FundLenderId FROM Stage.Fund;
        DELETE Fact.Lead;
        INSERT Fact.Lead (LeadId, dtPayReceived, IsUnwound, AF, GFI, TFI, SalesII, DivisionId, Division, SalesId, Salesperson, SalesCode, SalesOfficeId, LenderName, Profession, Facility, Arrangement, IsOB, IsOptical, IsNewCustomer, IRR, IRRInclFees, Term, MinIRR, PeriodId, PostcodeArea)
        SELECT LeadId, dtPayReceived, IsUnwound, AF, GFI, TFI, SalesII, DivisionId, Division, SalesId, Salesperson, SalesCode, SalesOfficeId, LenderName, Profession, Facility, Arrangement, IsOB, IsOptical, IsNewCustomer, IRR, IRRInclFees, Term, MinIRR, PeriodId, PostcodeArea FROM Stage.Lead;
        DELETE Fact.LeadPipeline;
        INSERT Fact.LeadPipeline (LeadId, SalesId, dtLead, LeadStatus, AF, GFI, dtAppAccept, dtActivation, IntroducerId, IntroducerManagedByHQ, IntroducerAccountManagerId)
        SELECT LeadId, SalesId, dtLead, LeadStatus, AF, GFI, dtAppAccept, dtActivation, IntroducerId, IntroducerManagedByHQ, IntroducerAccountManagerId FROM Stage.LeadPipeline;
        DELETE Fact.SupplierYear;
        INSERT Fact.SupplierYear (Year, IntroducerId, Supplier, Profession, AccountManagerId, ManagedByHQ, AmountFinanced)
        SELECT Year, IntroducerId, Supplier, Profession, AccountManagerId, ManagedByHQ, AmountFinanced FROM Stage.SupplierYear;
        DELETE Fact.BookPosition;
        INSERT Fact.BookPosition (LeadId, IsOB, RemainingCapital, CustomerKey, PostcodeArea)
        SELECT LeadId, IsOB, RemainingCapital, CustomerKey, PostcodeArea FROM Stage.BookPosition;
        DELETE Fact.FundingLine;
        INSERT Fact.FundingLine (Source, SourceId, LeadId, EventDate, DivisionId, Amount, Term, Rate, IsBulkMove)
        SELECT Source, SourceId, LeadId, EventDate, DivisionId, Amount, Term, Rate, IsBulkMove FROM Stage.FundingLine;
        DELETE Auth.UserAccess;
        INSERT Auth.UserAccess (UserId, UserName, FullName, Email, SalesOfficeId, IsRestrictedToOffice, AccountLocked, CanViewKpis, CanViewBadDebt, CanViewExposure)
        SELECT UserId, UserName, FullName, Email, SalesOfficeId, IsRestrictedToOffice, AccountLocked, CanViewKpis, CanViewBadDebt, CanViewExposure FROM Stage.UserAccess;

        IF @Scope = 'Nightly'
        BEGIN
            DELETE Fact.BadDebtYear;
            INSERT Fact.BadDebtYear (IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount)
            SELECT IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount FROM Stage.BadDebtYear;
            DELETE Fact.Exposure;
            INSERT Fact.Exposure (GroupingKey, FundLenderId, FundId, RowNumber, Id, Name, LeadCount, RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType)
            SELECT GroupingKey, FundLenderId, FundId, RowNumber, Id, Name, LeadCount, RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType FROM Stage.Exposure;
        END

        UPDATE Ref.Setting
        SET    ReportDate          = @ReportDate,
               LastFrequentLoadUtc = SYSUTCDATETIME(),
               LastNightlyLoadUtc  = CASE WHEN @Scope = 'Nightly' THEN SYSUTCDATETIME() ELSE LastNightlyLoadUtc END
        WHERE  SettingId = 1;
        COMMIT TRANSACTION;

        UPDATE Ref.LoadRun SET FinishedUtc = SYSUTCDATETIME(), Succeeded = 1 WHERE LoadRunId = @RunId;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        UPDATE Ref.LoadRun
        SET    FinishedUtc = SYSUTCDATETIME(), Succeeded = 0, Error = LEFT(ERROR_MESSAGE(), 2000)
        WHERE  LoadRunId = @RunId;
        THROW;
    END CATCH
END
""");

            // Rpt.fnReportDate
            migrationBuilder.Sql("""
CREATE FUNCTION Rpt.fnReportDate ()
RETURNS date
AS
BEGIN
    RETURN (SELECT ReportDate FROM Ref.Setting WHERE SettingId = 1);
END
""");

            // Rpt.fnPeriodDates
            migrationBuilder.Sql("""
CREATE FUNCTION Rpt.fnPeriodDates
(
    @AsOf           date,
    @AnalysisPeriod nvarchar(10)          -- 'Year' | 'Quarter' | 'Month'
)
RETURNS TABLE
AS
RETURN
(
    WITH AnchorNow AS
    (
        SELECT * FROM Dim.Period WHERE @AsOf BETWEEN PeriodStartDate AND PeriodEndDate
    ),
    AnchorPrior AS
    (
        SELECT Pp.*
        FROM   AnchorNow N
               INNER JOIN Dim.Period Pp ON Pp.YearNumber = N.YearNumber - 1 AND Pp.PeriodNumber = N.PeriodNumber
    )
    SELECT
        1 AS PeriodId,
        CASE @AnalysisPeriod WHEN 'Year' THEN N.YearName WHEN 'Quarter' THEN N.QuarterName ELSE N.PeriodName END AS AnalysisPeriodName,
        CASE @AnalysisPeriod
            WHEN 'Year'    THEN DATEFROMPARTS(N.YearNumber, 1, 1)
            WHEN 'Quarter' THEN (SELECT MIN(X.PeriodStartDate) FROM Dim.Period X WHERE X.QuarterId = N.QuarterId)
            ELSE                N.PeriodStartDate
        END AS DateFrom,
        @AsOf AS DateTo
    FROM AnchorNow N
    UNION ALL
    SELECT
        2,
        CASE @AnalysisPeriod WHEN 'Year' THEN N.YearName WHEN 'Quarter' THEN N.QuarterName ELSE N.PeriodName END,
        CASE @AnalysisPeriod
            WHEN 'Year'    THEN DATEFROMPARTS(N.YearNumber, 1, 1)
            WHEN 'Quarter' THEN (SELECT MIN(X.PeriodStartDate) FROM Dim.Period X WHERE X.QuarterId = N.QuarterId)
            ELSE                N.PeriodStartDate
        END,
        DATEADD(YEAR, -1, @AsOf)
    FROM AnchorPrior N
);
""");

            // Rpt.spFilterOptions
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spFilterOptions
(
    @List varchar(20)      -- 'Divisions' | 'Salespeople' | 'Arrangements' | 'Years' | 'Offices'
)
AS
BEGIN
    SET NOCOUNT ON;

    IF @List = 'Divisions'
        SELECT DISTINCT DivisionId AS Id, Division AS [Name]
        FROM   Fact.Lead WHERE DivisionId IS NOT NULL ORDER BY [Name];

    ELSE IF @List = 'Salespeople'
        SELECT SalesId AS Id, Salesperson AS [Name]
        FROM   Dim.Salesperson WHERE IsActive = 1 ORDER BY Salesperson;

    ELSE IF @List = 'Arrangements'
        SELECT DISTINCT 0 AS Id, Arrangement AS [Name]
        FROM   Fact.Lead WHERE Arrangement IS NOT NULL ORDER BY [Name];

    ELSE IF @List = 'Years'
        SELECT DISTINCT YEAR(dtPayReceived) AS Id, CAST(YEAR(dtPayReceived) AS nvarchar(4)) AS [Name]
        FROM   Fact.Lead ORDER BY Id DESC;

    ELSE IF @List = 'Offices'
        SELECT SalesOfficeId AS Id, OfficeName AS [Name]
        FROM   Dim.SalesOffice WHERE IsActive = 1 ORDER BY OrderInt, OfficeName;
END
""");

            // Rpt.spSalesSummary
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spSalesSummary
(
    @AnalysisPeriod nvarchar(10),
    @DivisionId     int = -1,
    @SalespersonId  int = -1,        -- -1 = everyone (income = TFI); one person = GFI + their managed commission
    @Optical        bit = NULL,
    @SalesOfficeId  int = NULL       -- NULL = every office
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AsOf date = Rpt.fnReportDate();

    WITH Windows AS
    (
        SELECT PeriodId, AnalysisPeriodName, DateFrom, DateTo
        FROM   Rpt.fnPeriodDates(@AsOf, @AnalysisPeriod)
    ),
    Scoped AS
    (
        SELECT F.LeadId, F.dtPayReceived, F.IsNewCustomer, F.AF,
               CASE WHEN @SalespersonId = -1 THEN F.TFI ELSE F.GFI + F.SalesII END AS Income
        FROM   Fact.Lead F
        WHERE  F.IsUnwound = 0
          AND  (@SalespersonId = -1    OR F.SalesId       = @SalespersonId)
          AND  (@DivisionId    = -1    OR F.DivisionId    = @DivisionId)
          AND  (@Optical       IS NULL OR F.IsOptical     = @Optical)
          AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    )
    SELECT
        W.PeriodId,
        W.AnalysisPeriodName,
        W.DateFrom,
        W.DateTo,
        ISNULL(SUM(S.Income), 0)                                                     AS TFI,
        ISNULL(SUM(S.AF), 0)                                                         AS AF,
        CASE WHEN ISNULL(SUM(S.AF), 0) = 0 THEN 0 ELSE SUM(S.Income) / SUM(S.AF) END AS Margin,
        COUNT(S.LeadId)                                                              AS Leads,
        ISNULL(SUM(CASE WHEN S.IsNewCustomer = 1 THEN 1 ELSE 0 END), 0)              AS NewLeads,
        COUNT(S.LeadId) - ISNULL(SUM(CASE WHEN S.IsNewCustomer = 1 THEN 1 ELSE 0 END), 0) AS ExistingLeads
    FROM   Windows W
           LEFT JOIN Scoped S ON S.dtPayReceived >= W.DateFrom AND S.dtPayReceived <= W.DateTo
    GROUP BY W.PeriodId, W.AnalysisPeriodName, W.DateFrom, W.DateTo
    ORDER BY W.PeriodId;
END
""");

            // Rpt.spYearOnYear
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spYearOnYear
(
    @Measure       nvarchar(4)   = 'AF',
    @Interval      nvarchar(10)  = 'Month',
    @DivisionId    int           = -1,
    @SalespersonId int           = -1,
    @Arrangement   nvarchar(100) = NULL,
    @SalesOfficeId int           = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        YEAR(F.dtPayReceived)                                                   AS [Year],
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, F.dtPayReceived)
             ELSE MONTH(F.dtPayReceived) END                                    AS Period,
        SUM(CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END)                AS Total,
        SUM(CASE WHEN F.IsOB = 1 THEN CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END ELSE 0 END) AS TotalOB,
        SUM(CASE WHEN F.IsOB = 0 THEN CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END ELSE 0 END) AS TotalBrokered
    FROM   Fact.Lead F
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) > YEAR(Rpt.fnReportDate()) - 10
      AND  (@DivisionId    = -1    OR F.DivisionId    = @DivisionId)
      AND  (@SalespersonId = -1    OR F.SalesId       = @SalespersonId)
      AND  (@Arrangement   IS NULL OR F.Arrangement   = @Arrangement)
      AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    GROUP BY
        YEAR(F.dtPayReceived),
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, F.dtPayReceived) ELSE MONTH(F.dtPayReceived) END
    ORDER BY [Year], Period;
END
""");

            // Rpt.spBusinessAnalysis
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spBusinessAnalysis
(
    @Dimension     nvarchar(20),
    @Measure       nvarchar(4) = 'AF',
    @Quarter       int         = 0,       -- 0 = all quarters, else 1-4
    @SalesOfficeId int         = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AsOf date = Rpt.fnReportDate();

    WITH Base AS
    (
        SELECT
            YEAR(F.dtPayReceived) AS Yr,
            CASE @Dimension
                WHEN 'Profession'  THEN F.Profession
                WHEN 'Facility'    THEN F.Facility
                WHEN 'Arrangement' THEN F.Arrangement
                WHEN 'Division'    THEN F.Division
            END AS DimValue,
            CASE WHEN MONTH(F.dtPayReceived) < MONTH(@AsOf)
               OR (MONTH(F.dtPayReceived) = MONTH(@AsOf) AND DAY(F.dtPayReceived) <= DAY(@AsOf))
             THEN 1 ELSE 0 END AS InYtd,
            CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END AS Amount
        FROM   Fact.Lead F
        WHERE  F.IsUnwound = 0
          AND  (@Quarter = 0 OR DATEPART(QUARTER, F.dtPayReceived) = @Quarter)
          AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    )
    SELECT Yr, DimValue,
           SUM(Amount)                                     AS FullTotal,
           SUM(CASE WHEN InYtd = 1 THEN Amount ELSE 0 END) AS YtdTotal
    FROM   Base
    WHERE  DimValue IS NOT NULL
    GROUP BY Yr, DimValue
    ORDER BY Yr, SUM(Amount) DESC;
END
""");

            // Rpt.spBusinessAnalysisByLender
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spBusinessAnalysisByLender
(
    @SalesOfficeId int = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AsOf date = Rpt.fnReportDate();

    WITH Paid AS
    (
        SELECT
            YEAR(F.dtPayReceived)              AS Yr,
            DATEPART(QUARTER, F.dtPayReceived) AS Qtr,
            F.LenderName                       AS Lender,
            F.AF,
            F.TFI,
            CASE WHEN MONTH(F.dtPayReceived) < MONTH(@AsOf)
               OR (MONTH(F.dtPayReceived) = MONTH(@AsOf) AND DAY(F.dtPayReceived) <= DAY(@AsOf))
             THEN 1 ELSE 0 END AS InYtd
        FROM   Fact.Lead F
        WHERE  F.IsUnwound = 0
          AND  F.LenderName IS NOT NULL
          AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    )
    SELECT
        Yr                                              AS [Year],
        CASE WHEN GROUPING(Qtr) = 1 THEN 0 ELSE Qtr END AS [Quarter],
        Lender,
        SUM(AF)                                         AS AF,
        SUM(CASE WHEN InYtd = 1 THEN AF  ELSE 0 END)    AS AFToDate,
        SUM(TFI)                                        AS TFI,
        SUM(CASE WHEN InYtd = 1 THEN TFI ELSE 0 END)    AS TFIToDate
    FROM   Paid
    GROUP BY GROUPING SETS ((Yr, Qtr, Lender), (Yr, Lender))
    ORDER BY [Year], [Quarter], AF DESC, Lender;
END
""");

            // Rpt.spSalesLeagueTable
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spSalesLeagueTable
(
    @Year    int,
    @Measure nvarchar(4) = 'TFI'
)
AS
BEGIN
    SET NOCOUNT ON;

    WITH Core AS        -- the year's reportable salespeople (Cfg.SalespersonScope replaces the hard-coded ids)
    (
        SELECT DISTINCT SalesId
        FROM   Cfg.SalespersonScope
        WHERE  InLeague = 1 AND @Year >= FromYear AND (ToYear IS NULL OR @Year <= ToYear)
    )
    SELECT
        @Year                                                     AS Yr,
        F.SalesId,
        F.Salesperson,
        SUM(CASE WHEN @Measure = 'AF' THEN F.AF ELSE F.TFI END)   AS MeasureValue,
        CAST(MAX(CASE WHEN C.SalesId IS NULL THEN 0 ELSE 1 END) AS bit) AS IsCore
    FROM   Fact.Lead F
           LEFT JOIN Core C ON C.SalesId = F.SalesId
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) = @Year
    GROUP BY F.SalesId, F.Salesperson
    ORDER BY IsCore DESC, MeasureValue DESC;
END
""");

            // Rpt.spSalesLeagueTimelapse
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spSalesLeagueTimelapse
(
    @Year    int,
    @Measure nvarchar(4) = 'TFI'
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @YearStart date = DATEFROMPARTS(@Year, 1, 1);

    WITH Core AS        -- the year's reportable salespeople (Cfg.SalespersonScope replaces the hard-coded ids)
    (
        SELECT DISTINCT SalesId
        FROM   Cfg.SalespersonScope
        WHERE  InLeague = 1 AND @Year >= FromYear AND (ToYear IS NULL OR @Year <= ToYear)
    )
    SELECT
        F.SalesId,
        F.Salesperson,
        DATEDIFF(WEEK, @YearStart, F.dtPayReceived) + 1                                           AS WeekNo,
        DATEADD(DAY, 6, DATEADD(WEEK, DATEDIFF(WEEK, @YearStart, F.dtPayReceived), @YearStart))    AS WeekEnd,
        SUM(CASE WHEN @Measure = 'AF' THEN F.AF ELSE F.TFI END)                                   AS WeekValue
    FROM   Fact.Lead F
           INNER JOIN Core C ON C.SalesId = F.SalesId          -- the same people as the league
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) = @Year
    GROUP BY F.SalesId, F.Salesperson,
             DATEDIFF(WEEK, @YearStart, F.dtPayReceived),
             DATEADD(DAY, 6, DATEADD(WEEK, DATEDIFF(WEEK, @YearStart, F.dtPayReceived), @YearStart))
    ORDER BY WeekNo, F.SalesId;
END
""");

            // Rpt.spSalesTargets
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spSalesTargets
(
    @Year    int,
    @Measure nvarchar(4) = 'TFI'
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    DECLARE @AsAt      date = Rpt.fnReportDate();
    DECLARE @YearStart date = DATEFROMPARTS(@Year, 1, 1);
    DECLARE @YearEnd   date = DATEFROMPARTS(@Year, 12, 31);
    -- a past year is fully elapsed, a future one has not started
    DECLARE @Effective date = CASE WHEN @AsAt > @YearEnd THEN @YearEnd
                                   WHEN @AsAt < @YearStart THEN @YearStart
                                   ELSE @AsAt END;

    DECLARE @WdTotal   int = (SELECT COUNT(*) FROM Dim.WorkingDay WHERE WorkingDay BETWEEN @YearStart AND @YearEnd);
    DECLARE @WdElapsed int = (SELECT COUNT(*) FROM Dim.WorkingDay WHERE WorkingDay BETWEEN @YearStart AND @Effective);

    DECLARE @Fraction decimal(18,10) =
        CASE WHEN @WdTotal > 0
             THEN CAST(@WdElapsed AS decimal(18,10)) / CAST(@WdTotal AS decimal(18,10))
             ELSE CAST(DATEDIFF(DAY, @YearStart, @Effective) + 1 AS decimal(18,10))
                / CAST(DATEDIFF(DAY, @YearStart, @YearEnd)   + 1 AS decimal(18,10))
        END;

    SELECT
        @Year                                                                          AS Yr,
        T.SalesId,
        S.Salesperson,
        CAST(CASE WHEN @Measure = 'AF' THEN T.AFTarget ELSE T.TFITarget END AS decimal(18,2)) AS YearTarget,
        CAST(ROUND(CASE WHEN @Measure = 'AF' THEN T.AFTarget ELSE T.TFITarget END * @Fraction, 2) AS decimal(18,2)) AS YtdTarget,
        @Fraction                                                                      AS ElapsedFraction,
        CAST(CASE WHEN @WdTotal > 0 THEN 1 ELSE 0 END AS bit)                          AS UsesWorkingDays
    FROM   Dim.SalesTarget T
           INNER JOIN Dim.Salesperson S ON S.SalesId = T.SalesId
    WHERE  T.[Year] = @Year
    ORDER BY YearTarget DESC;

    COMMIT TRANSACTION;
END
""");

            // Rpt.spSalesProductivity
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spSalesProductivity
(
    @SalespersonId int = -1
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Year int = YEAR(Rpt.fnReportDate());

    -- statuses still waiting for approval, and approved but waiting for activation
    DECLARE @AwaitingApproval   TABLE (LeadStatus nvarchar(50) PRIMARY KEY);
    DECLARE @AwaitingActivation TABLE (LeadStatus nvarchar(50) PRIMARY KEY);
    INSERT @AwaitingApproval VALUES ('LEAD'), ('PROPOSAL'), ('PROPOSAL ACCEPTED'), ('APPLICATION');
    INSERT @AwaitingActivation VALUES ('APPLICATION ACCEPTED'), ('CLIENT APPROVAL LETTER'), ('INVOICE REQUEST LETTER'),
        ('INVOICE RECEIVED'), ('DOCUMENTS SENT'), ('DOCUMENTS SIGNED'), ('PRE-LEASE'), ('PRE/PART PAYMENT'), ('SENT FOR PAYMENT');

    WITH Years AS
    (
        SELECT DISTINCT YearNumber AS Yr FROM Dim.Period WHERE YearNumber > @Year - 4
    ),
    People AS          -- the reportable salespeople x those years
    (
        SELECT DISTINCT SP.SalesId, SP.Salesperson, Y.Yr
        FROM   Dim.Salesperson SP
               INNER JOIN Cfg.SalespersonScope SC ON SC.SalesId = SP.SalesId AND SC.InProductivity = 1
               INNER JOIN Years Y ON Y.Yr >= SC.FromYear AND (SC.ToYear IS NULL OR Y.Yr <= SC.ToYear)
    ),
    Leads AS           -- by the year the lead came in
    (
        SELECT
            L.SalesId, YEAR(L.dtLead) AS Yr,
            SUM(L.AF) AS TotalLead_AF, COUNT(*) AS TotalLead_Count,
            SUM(CASE WHEN L.IntroducerId IS NULL THEN 1 ELSE 0 END)    AS NonIntroduced_Count,
            SUM(CASE WHEN L.IntroducerId IS NULL THEN L.AF ELSE 0 END) AS NonIntroduced_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 1 THEN L.AF ELSE 0 END) AS HQLead_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 1 THEN 1 ELSE 0 END)    AS HQLead_Count,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) =  L.SalesId THEN L.AF ELSE 0 END) AS Self_ThisSales_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) =  L.SalesId THEN 1 ELSE 0 END)    AS Self_ThisSales_Count,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) <> L.SalesId THEN L.AF ELSE 0 END) AS Self_OtherSales_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) <> L.SalesId THEN 1 ELSE 0 END)    AS Self_OtherSales_Count,
            SUM(CASE WHEN AA.LeadStatus IS NOT NULL THEN 1 ELSE 0 END)    AS AWApproval_AsSubset_Count,
            SUM(CASE WHEN AA.LeadStatus IS NOT NULL THEN L.AF ELSE 0 END) AS AWApproval_AsSubset_AF,
            SUM(CASE WHEN L.dtAppAccept IS NOT NULL THEN 1 ELSE 0 END)    AS Approved_AsSubset_Count,
            SUM(CASE WHEN AV.LeadStatus IS NOT NULL THEN 1 ELSE 0 END)    AS AWActivation_AsSubset_Count,
            SUM(CASE WHEN AV.LeadStatus IS NOT NULL THEN L.AF ELSE 0 END) AS AWActivation_AsSubset_AF,
            SUM(CASE WHEN L.dtActivation IS NOT NULL THEN 1 ELSE 0 END)    AS Activated_AsSubset_Count,
            SUM(CASE WHEN L.dtActivation IS NOT NULL THEN L.AF ELSE 0 END) AS Activated_AsSubset_AF
        FROM   Fact.LeadPipeline L
               LEFT JOIN @AwaitingApproval   AA ON AA.LeadStatus = L.LeadStatus
               LEFT JOIN @AwaitingActivation AV ON AV.LeadStatus = L.LeadStatus
        WHERE  YEAR(L.dtLead) > @Year - 4
        GROUP BY L.SalesId, YEAR(L.dtLead)
    ),
    Approved AS        -- by the year of approval
    (
        SELECT SalesId, YEAR(dtAppAccept) AS Yr, COUNT(*) AS Approved_Year_Count
        FROM   Fact.LeadPipeline WHERE YEAR(dtAppAccept) > @Year - 4
        GROUP BY SalesId, YEAR(dtAppAccept)
    ),
    Activated AS       -- by the year of activation
    (
        SELECT SalesId, YEAR(dtActivation) AS Yr, COUNT(*) AS Activated_Year_Count,
               SUM(AF) AS Activated_Year_AF, SUM(GFI) AS Activated_Year_GFI
        FROM   Fact.LeadPipeline WHERE YEAR(dtActivation) > @Year - 4
        GROUP BY SalesId, YEAR(dtActivation)
    ),
    ForOthers AS       -- leads their own suppliers brought in for colleagues
    (
        SELECT IntroducerAccountManagerId AS SalesId, YEAR(dtLead) AS Yr, SUM(AF) AS AF
        FROM   Fact.LeadPipeline
        WHERE  IntroducerId IS NOT NULL
          AND  ISNULL(IntroducerManagedByHQ, 0) = 0
          AND  SalesId <> IntroducerAccountManagerId
          AND  YEAR(dtLead) > @Year - 4
        GROUP BY IntroducerAccountManagerId, YEAR(dtLead)
    ),
    R AS
    (
        SELECT
            P.SalesId, P.Salesperson, P.Yr,
            ISNULL(L.TotalLead_AF, 0) AS TotalLead_AF,                 ISNULL(L.TotalLead_Count, 0) AS TotalLead_Count,
            ISNULL(L.NonIntroduced_AF, 0) AS NonIntroduced_AF,         ISNULL(L.NonIntroduced_Count, 0) AS NonIntroduced_Count,
            ISNULL(L.HQLead_AF, 0) AS HQLead_AF,                       ISNULL(L.HQLead_Count, 0) AS HQLead_Count,
            ISNULL(L.Self_ThisSales_AF, 0) AS Self_ThisSales_AF,       ISNULL(L.Self_ThisSales_Count, 0) AS Self_ThisSales_Count,
            ISNULL(L.Self_OtherSales_AF, 0) AS Self_OtherSales_AF,     ISNULL(L.Self_OtherSales_Count, 0) AS Self_OtherSales_Count,
            ISNULL(L.AWApproval_AsSubset_AF, 0) AS AWApproval_AsSubset_AF,
            ISNULL(L.AWApproval_AsSubset_Count, 0) AS AWApproval_AsSubset_Count,
            ISNULL(A.Approved_Year_Count, 0) AS Approved_Year_Count,
            ISNULL(L.Approved_AsSubset_Count, 0) AS Approved_AsSubset_Count,
            ISNULL(L.AWActivation_AsSubset_AF, 0) AS AWActivation_AsSubset_AF,
            ISNULL(L.AWActivation_AsSubset_Count, 0) AS AWActivation_AsSubset_Count,
            ISNULL(V.Activated_Year_AF, 0) AS Activated_Year_AF,       ISNULL(V.Activated_Year_Count, 0) AS Activated_Year_Count,
            ISNULL(V.Activated_Year_GFI, 0) AS Activated_Year_GFI,
            ISNULL(L.Activated_AsSubset_AF, 0) AS Activated_AsSubset_AF,
            ISNULL(L.Activated_AsSubset_Count, 0) AS Activated_AsSubset_Count,
            ISNULL(O.AF, 0) AS GeneratedForOthers_Year_AF
        FROM   People P
               LEFT JOIN Leads     L ON L.SalesId = P.SalesId AND L.Yr = P.Yr
               LEFT JOIN Approved  A ON A.SalesId = P.SalesId AND A.Yr = P.Yr
               LEFT JOIN Activated V ON V.SalesId = P.SalesId AND V.Yr = P.Yr
               LEFT JOIN ForOthers O ON O.SalesId = P.SalesId AND O.Yr = P.Yr
    )
    SELECT
        CAST(ROW_NUMBER() OVER (ORDER BY R.Salesperson, R.Yr DESC) AS int) AS Id,
        R.SalesId, R.Salesperson, R.Yr AS [Year],
        R.TotalLead_AF, R.TotalLead_Count, R.NonIntroduced_AF, R.NonIntroduced_Count, R.HQLead_AF, R.HQLead_Count,
        R.Self_ThisSales_AF, R.Self_ThisSales_Count, R.Self_OtherSales_AF, R.Self_OtherSales_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE (R.NonIntroduced_AF + R.Self_ThisSales_AF) / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS SelfGen_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE (R.NonIntroduced_Count + R.Self_ThisSales_Count) / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS SelfGen_Percent_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE R.HQLead_AF / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS HQ_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE R.HQLead_Count / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS HQ_Percent_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE R.Self_OtherSales_AF / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS OtherSales_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE R.Self_OtherSales_Count / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS OtherSales_Percent_Count,
        R.AWApproval_AsSubset_AF, R.AWApproval_AsSubset_Count, R.Approved_Year_Count, R.Approved_AsSubset_Count,
        CAST(CASE WHEN R.TotalLead_Count - R.AWApproval_AsSubset_Count = 0 THEN 0
                  ELSE R.Approved_AsSubset_Count / ((R.TotalLead_Count - R.AWApproval_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS L_APR_Percent_Count,
        R.AWActivation_AsSubset_Count, R.AWActivation_AsSubset_AF,
        R.Activated_Year_Count, R.Activated_Year_AF, R.Activated_Year_GFI,
        R.Activated_AsSubset_Count, R.Activated_AsSubset_AF,
        CAST(CASE WHEN R.TotalLead_AF - R.AWApproval_AsSubset_AF - R.AWActivation_AsSubset_AF = 0 THEN 0
                  ELSE R.Activated_AsSubset_AF / ((R.TotalLead_AF - R.AWApproval_AsSubset_AF - R.AWActivation_AsSubset_AF) / 1.0000) END AS decimal(18,2)) AS L_ACT_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count - R.AWApproval_AsSubset_Count - R.AWActivation_AsSubset_Count = 0 THEN 0
                  ELSE R.Activated_AsSubset_Count / ((R.TotalLead_Count - R.AWApproval_AsSubset_Count - R.AWActivation_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS L_ACT_Percent_Count,
        CAST(CASE WHEN R.Approved_AsSubset_Count - R.AWActivation_AsSubset_Count = 0 THEN 0
                  ELSE R.Activated_AsSubset_Count / ((R.Approved_AsSubset_Count - R.AWActivation_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS APR_ACT_Percent_Count,
        R.GeneratedForOthers_Year_AF,
        SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr)                   AS TotalLeadAll_AF,
        SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF  AS TotalLeadOthers_AF,
        CAST(CASE WHEN SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF = 0 THEN 0
                  ELSE R.GeneratedForOthers_Year_AF / (SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF) END AS decimal(18,2)) AS GenForOthers_Percent_AF
    FROM   R
    WHERE  @SalespersonId = -1 OR R.SalesId = @SalespersonId
    ORDER BY R.Salesperson, R.Yr DESC;
END
""");

            // Rpt.spRecordPerformances
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spRecordPerformances
(
    @Grain      nvarchar(12),        -- 'Transaction' | 'Month' | 'Quarter' | 'Annual'
    @DivisionId int = -1
)
AS
BEGIN
    SET NOCOUNT ON;

    WITH LeadIncome AS
    (
        SELECT F.LeadId, F.dtPayReceived, F.SalesId, F.Salesperson, F.GFI + F.SalesII AS Income
        FROM   Fact.Lead F
        WHERE  F.IsUnwound = 0
          AND  (@DivisionId = -1 OR F.DivisionId = @DivisionId)
    ),
    Labelled AS
    (
        SELECT
            YEAR(dtPayReceived) AS Yr,
            CASE @Grain
                WHEN 'Transaction' THEN LEFT(DATENAME(MONTH, dtPayReceived), 3) + '-' + CAST(YEAR(dtPayReceived) AS varchar(4))
                WHEN 'Month'       THEN LEFT(DATENAME(MONTH, dtPayReceived), 3) + '-' + CAST(YEAR(dtPayReceived) AS varchar(4))
                WHEN 'Quarter'     THEN 'Q' + CAST(DATEPART(QUARTER, dtPayReceived) AS varchar(1)) + ' ' + CAST(YEAR(dtPayReceived) AS varchar(4))
                ELSE                    CAST(YEAR(dtPayReceived) AS varchar(4))
            END AS Period,
            LeadId, SalesId, Salesperson, Income
        FROM LeadIncome
    )
    SELECT Yr, Period, SalesId, Salesperson, Income AS TFI          -- one row per deal
    FROM   Labelled
    WHERE  @Grain = 'Transaction'
    UNION ALL
    SELECT Yr, Period, SalesId, Salesperson, SUM(Income)            -- per salesperson per month / quarter / year
    FROM   Labelled
    WHERE  @Grain <> 'Transaction'
    GROUP BY Yr, Period, SalesId, Salesperson
    ORDER BY TFI DESC;
END
""");

            // Rpt.spSupplierFigures
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spSupplierFigures
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ReportYear int = YEAR(Rpt.fnReportDate());

    SELECT [Year], IntroducerId, Supplier, Profession, AccountManagerId, ManagedByHQ, AmountFinanced
    FROM   Fact.SupplierYear
    WHERE  [Year] BETWEEN @ReportYear - 5 AND @ReportYear;
END
""");

            // Rpt.spHeatMap
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spHeatMap
(
    @Years       varchar(200),             -- '2025,2026'
    @Arrangement varchar(10) = 'OwnBook'   -- 'All' | 'OwnBook' | 'Brokered'
)
AS
BEGIN
    SET NOCOUNT ON;

    -- the chosen years (XML split works below compatibility level 130)
    DECLARE @Yr  TABLE (Yr int PRIMARY KEY);
    DECLARE @xml xml = CAST('<y>' + REPLACE(@Years, ',', '</y><y>') + '</y>' AS xml);
    INSERT @Yr (Yr)
    SELECT DISTINCT TRY_CAST(LTRIM(RTRIM(t.v.value('.', 'varchar(10)'))) AS int)
    FROM   @xml.nodes('/y') t(v)
    WHERE  TRY_CAST(LTRIM(RTRIM(t.v.value('.', 'varchar(10)'))) AS int) IS NOT NULL;

    WITH Cap AS            -- the serviced book now (the years do not apply)
    (
        SELECT B.PostcodeArea AS AreaCode,
               SUM(B.RemainingCapital) AS RemainingCapital,
               SUM(CASE WHEN B.RemainingCapital > 0 THEN 1 ELSE 0 END) AS ActiveLeads,
               COUNT(DISTINCT CASE WHEN B.RemainingCapital > 0 THEN B.CustomerKey END) AS ActiveCustomers
        FROM   Fact.BookPosition B
        WHERE  @Arrangement = 'All'
           OR  (@Arrangement = 'OwnBook'  AND B.IsOB = 1)
           OR  (@Arrangement = 'Brokered' AND B.IsOB = 0)
        GROUP BY B.PostcodeArea
    ),
    Sales AS               -- paid out in the chosen years
    (
        SELECT F.PostcodeArea AS AreaCode, SUM(F.AF) AS AF, SUM(F.TFI) AS TFI, COUNT(*) AS ActivatedLeads
        FROM   Fact.Lead F
        WHERE  F.IsUnwound = 0
          AND  YEAR(F.dtPayReceived) IN (SELECT Yr FROM @Yr)
          AND  (@Arrangement = 'All'
             OR (@Arrangement = 'OwnBook'  AND ISNULL(F.IsOB, 0) = 1)
             OR (@Arrangement = 'Brokered' AND ISNULL(F.IsOB, 0) = 0))
        GROUP BY F.PostcodeArea
    )
    SELECT
        ISNULL(PA.Region, 'Unmapped')     AS Region,
        ISNULL(PA.District, 'Unmapped')   AS District,
        ISNULL(Areas.AreaCode, 'U')       AS PostCodeArea,
        ISNULL(Cap.RemainingCapital, 0)   AS RemainingCapital,
        ISNULL(Cap.ActiveLeads, 0)        AS ActiveLeads,
        ISNULL(Cap.ActiveCustomers, 0)    AS ActiveCustomers,
        ISNULL(Sales.AF, 0)               AS AF,
        ISNULL(Sales.TFI, 0)              AS TFI,
        ISNULL(Sales.ActivatedLeads, 0)   AS ActivatedLeads
    FROM   (SELECT AreaCode FROM Cap UNION SELECT AreaCode FROM Sales) Areas
           LEFT JOIN Cap                  ON Cap.AreaCode   = Areas.AreaCode
           LEFT JOIN Sales                ON Sales.AreaCode = Areas.AreaCode
           LEFT JOIN Cfg.PostcodeArea PA  ON PA.AreaCode    = Areas.AreaCode
    ORDER BY Region, District;
END
""");

            // Rpt.spMarginAnalysisByDateRange
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spMarginAnalysisByDateRange
(
    @StartDate    date,
    @EndDate      date,
    @DivisionId   int,
    @BuyRate      decimal(18,4) OUTPUT,
    @SellRate     decimal(18,4) OUTPUT,
    @Margin       decimal(18,4) OUTPUT,
    @TotalFunding decimal(18,2) OUTPUT,
    @TotalAF      decimal(18,2) OUTPUT
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    DECLARE @AF decimal(18,2), @AFAll decimal(18,2), @SellTermRate decimal(38,6), @SellTerm decimal(38,6);
    DECLARE @BuyAmount decimal(38,6), @BuyTermRate decimal(38,6), @BuyTerm decimal(38,6), @Ratio decimal(18,4);

    -- 1. sell side: own-book leads paid out in the range (unwound ones included, as today)
    SELECT
        @AF           = SUM(CASE WHEN @DivisionId = -1 OR DivisionId = @DivisionId THEN AF ELSE 0 END),
        @SellTermRate = SUM(CASE WHEN @DivisionId = -1 OR DivisionId = @DivisionId THEN AF * Term * (IRR / 100) ELSE 0 END),
        @SellTerm     = SUM(CASE WHEN @DivisionId = -1 OR DivisionId = @DivisionId THEN AF * Term ELSE 0 END),
        @AFAll        = SUM(AF)
    FROM   Fact.Lead
    WHERE  IsOB = 1 AND dtPayReceived BETWEEN @StartDate AND @EndDate;

    SET @Ratio = CASE WHEN ISNULL(@AFAll, 0) = 0 THEN 0 ELSE @AF / @AFAll END;

    -- 2. buy side: blocks shared out by the division's share; RCF and IWFA by their lead's division
    SELECT
        @BuyAmount   = SUM(X.Amount),
        @BuyTermRate = SUM(X.Amount * X.Term * X.Rate),
        @BuyTerm     = SUM(X.Amount * X.Term)
    FROM
    (
        SELECT CASE WHEN FL.Source = 'Block' THEN FL.Amount * @Ratio ELSE FL.Amount END AS Amount, FL.Term, FL.Rate
        FROM   Fact.FundingLine FL
        WHERE  FL.EventDate BETWEEN @StartDate AND @EndDate
          AND  FL.IsBulkMove = 0
          AND  (FL.Source = 'Block' OR @DivisionId = -1 OR FL.DivisionId = @DivisionId)
    ) X;

    SET @SellRate     = CASE WHEN ISNULL(@SellTerm, 0) = 0 THEN 0 ELSE @SellTermRate / @SellTerm END;
    SET @BuyRate      = CASE WHEN ISNULL(@BuyTerm, 0)  = 0 THEN 0 ELSE @BuyTermRate  / @BuyTerm  END;
    SET @Margin       = @SellRate - @BuyRate;
    SET @TotalFunding = ISNULL(@BuyAmount, 0);
    SET @TotalAF      = @AF;

    COMMIT TRANSACTION;
END
""");

            // Rpt.spMarginAnalysisByDays
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spMarginAnalysisByDays (@Days int, @DivisionId int)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @Start date = DATEADD(DAY, -@Days, GETDATE()), @End date = GETDATE();
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT;

    SELECT CONVERT(nvarchar(10), @Start, 120) + ' - ' + CONVERT(nvarchar(10), @End, 120) AS DateRange,
           @Buy AS BuyRate, @Sell AS SellRate, @Margin AS Margin, @Funding AS TotalFunding, @AF AS TotalAF;
    COMMIT TRANSACTION;
END
""");

            // Rpt.spMarginAnalysisByMonth
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spMarginAnalysisByMonth (@DivisionId int, @RollingMonth int)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @First date = CASE WHEN @RollingMonth = 1
                               THEN DATEFROMPARTS(YEAR(DATEADD(MONTH, -11, GETDATE())), MONTH(DATEADD(MONTH, -11, GETDATE())), 1)
                               ELSE DATEFROMPARTS(YEAR(GETDATE()), 1, 1) END;
    DECLARE @Out TABLE (YearInt int, MonthInt int, MonthString nvarchar(20), BuyRate decimal(18,4), SellRate decimal(18,4),
                        Margin decimal(18,4), TotalFunding decimal(18,4), TotalAF decimal(18,4), IsFuture bit,
                        OrderInt int, StartDate date, EndDate date);
    DECLARE @i int = 1, @Start date, @End date;
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    WHILE @i <= 13          -- 12 months, then the overall row
    BEGIN
        SET @Start = CASE WHEN @i < 13 THEN DATEADD(MONTH, @i - 1, @First) ELSE @First END;
        SET @End   = CASE WHEN @i < 13 THEN EOMONTH(@Start) ELSE EOMONTH(DATEADD(MONTH, 11, @First)) END;

        EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT;

        INSERT @Out VALUES (YEAR(@Start), CASE WHEN @i < 13 THEN MONTH(@Start) ELSE 13 END,
                            CASE WHEN @i < 13 THEN FORMAT(@Start, 'MMM yyyy') ELSE 'Overall' END,
                            @Buy, @Sell, @Margin, @Funding, ISNULL(@AF, 0),
                            CASE WHEN @i < 13 AND @Start > CAST(GETDATE() AS date) THEN 1 ELSE 0 END,
                            @i, @Start, @End);
        SET @i += 1;
    END

    SELECT * FROM @Out ORDER BY OrderInt;
    COMMIT TRANSACTION;
END
""");

            // Rpt.spMarginAnalysisByYear
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spMarginAnalysisByYear (@DivisionId int)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @Out TABLE (YearInt int, BuyRate decimal(18,4), SellRate decimal(18,4), Margin decimal(18,4),
                        TotalFunding decimal(18,4), TotalAF decimal(18,4));
    DECLARE @Year int = 2021, @Start date, @End date;
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    WHILE @Year <= YEAR(GETDATE())
    BEGIN
        SET @Start = DATEFROMPARTS(@Year, 1, 1);
        SET @End   = DATEFROMPARTS(@Year, 12, 31);
        EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT;
        INSERT @Out VALUES (@Year, @Buy, @Sell, @Margin, @Funding, @AF);
        SET @Year += 1;
    END

    SELECT * FROM @Out ORDER BY YearInt;
    COMMIT TRANSACTION;
END
""");

            // Rpt.spMinIRRAnalysis
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spMinIRRAnalysis
(
    @AnalysisPeriod nvarchar(10) = 'M_This',
    @DivisionId     int          = -1
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    DECLARE @Grain char(1) = CASE WHEN @AnalysisPeriod LIKE '%M%' THEN 'M' WHEN @AnalysisPeriod LIKE '%Q%' THEN 'Q' ELSE 'Y' END;
    DECLARE @This  bit     = CASE WHEN @AnalysisPeriod LIKE '%This%' THEN 1 ELSE 0 END;
    DECLARE @Today date    = CAST(GETDATE() AS date);
    DECLARE @From  date, @To date;

    IF @Grain = 'M'
    BEGIN
        DECLARE @M date = CASE WHEN @This = 1 THEN @Today ELSE DATEADD(MONTH, -1, @Today) END;
        SET @From = DATEFROMPARTS(YEAR(@M), MONTH(@M), 1);
        SET @To   = EOMONTH(@M);
    END
    ELSE IF @Grain = 'Q'
    BEGIN
        DECLARE @QId int = (SELECT QuarterId FROM Dim.Period WHERE @Today BETWEEN PeriodStartDate AND PeriodEndDate)
                         - CASE WHEN @This = 1 THEN 0 ELSE 1 END;
        SELECT @From = MIN(PeriodStartDate), @To = MAX(PeriodEndDate) FROM Dim.Period WHERE QuarterId = @QId;
    END
    ELSE
    BEGIN
        DECLARE @Y int = YEAR(@Today) - CASE WHEN @This = 1 THEN 0 ELSE 1 END;
        SET @From = DATEFROMPARTS(@Y, 1, 1);
        SET @To   = DATEFROMPARTS(@Y, 12, 31);
    END

    SELECT
        CASE @Grain WHEN 'Y' THEN P.YearName WHEN 'Q' THEN P.QuarterName
             ELSE LEFT(DATENAME(MONTH, F.dtPayReceived), 3) + ' ' + DATENAME(YEAR, F.dtPayReceived) END AS GroupByPeriod,
        F.LeadId, F.IRRInclFees, F.MinIRR, F.SalesId, F.Salesperson, F.Division,
        ISNULL(F.AF, 0)                                                    AS AF,
        ISNULL(F.IRRInclFees, 0) * ISNULL(F.AF, 0) * ISNULL(F.Term, 0)     AS Term_AF_IRR,
        ISNULL(F.AF, 0) * ISNULL(F.Term, 0)                                AS Term_AF
    INTO   #LeadData
    FROM   Fact.Lead F
           INNER JOIN Dim.Period P ON P.PeriodId = F.PeriodId
    WHERE  F.IsOB = 1
      AND  YEAR(F.dtPayReceived) > 2021
      AND  (@DivisionId = -1 OR F.DivisionId = @DivisionId)
      AND  F.dtPayReceived BETWEEN @From AND @To;

    -- per salesperson (active salespeople only, as today)
    SELECT
        ROW_NUMBER() OVER (ORDER BY L.Salesperson) AS OrderInt,
        L.GroupByPeriod, L.Division AS SalesDivision, L.Salesperson,
        COUNT(*)                                                                     AS TransactionsTotal,
        SUM(L.AF)                                                                    AS AFTotal,
        CASE WHEN SUM(L.Term_AF) = 0 THEN 0 ELSE SUM(L.Term_AF_IRR) / SUM(L.Term_AF) END AS IRRAverage,
        SUM(CASE WHEN L.IRRInclFees < L.MinIRR THEN 1 ELSE 0 END)                    AS TransactionsUnderMinIRR,
        SUM(CASE WHEN L.IRRInclFees < L.MinIRR THEN L.AF ELSE 0 END)                 AS AFUnderMinIRR
    INTO   #Results
    FROM   #LeadData L
           INNER JOIN Dim.Salesperson S ON S.SalesId = L.SalesId AND S.IsActive = 1
    GROUP BY L.GroupByPeriod, L.Division, L.Salesperson;

    -- the total row: every deal, and the people rows' below-minimum counts (as today)
    INSERT #Results
    SELECT 1000, GroupByPeriod, 'Total', 'Total', COUNT(*), SUM(AF),
           CASE WHEN SUM(Term_AF) = 0 THEN 0 ELSE SUM(Term_AF_IRR) / SUM(Term_AF) END,
           (SELECT SUM(TransactionsUnderMinIRR) FROM #Results),
           (SELECT SUM(AFUnderMinIRR) FROM #Results)
    FROM   #LeadData
    GROUP BY GroupByPeriod;

    SELECT OrderInt, GroupByPeriod, SalesDivision, Salesperson, TransactionsTotal,
           CAST(AFTotal AS decimal(18,0))                    AS AFTotal,
           CAST(IRRAverage AS decimal(18,4))                 AS IRRAverage,
           TransactionsUnderMinIRR,
           CAST(AFUnderMinIRR AS decimal(18,0))              AS AFUnderMinIRR,
           CAST(CASE WHEN TransactionsTotal > 0 THEN TransactionsUnderMinIRR * 1.0 / TransactionsTotal END AS decimal(18,4)) AS PercentTransactionsUnderMinIRR,
           CAST(CASE WHEN AFTotal > 0 THEN AFUnderMinIRR / AFTotal END AS decimal(18,4))                                     AS PercentAFUnderMinIRR
    FROM   #Results
    ORDER BY OrderInt;

    COMMIT TRANSACTION;
END
""");

            // Rpt.spBadDebt
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spBadDebt
(
    @IsAutoApproved int = 2
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, BadDebtYear, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount
    FROM   Fact.BadDebtYear
    WHERE  IsAutoApproved = @IsAutoApproved
    ORDER BY BadDebtYear;
END
""");

            // Rpt.spExposure
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spExposure
(
    @CustomerBusinessGroup int = NULL,     -- NULL combined, 0 customer, 1 business group
    @FundLenderId          int = NULL,
    @FundId                int = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT RowNumber, Id, [Name], LeadCount, RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType
    FROM   Fact.Exposure
    WHERE  GroupingKey  = ISNULL(@CustomerBusinessGroup, 2)
      AND  FundLenderId = ISNULL(@FundLenderId, 0)
      AND  FundId       = ISNULL(@FundId, 0)
    ORDER BY RowNumber;
END
""");

            // Rpt.spExposureFilters
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spExposureFilters
(
    @List         varchar(10),        -- 'Lenders' | 'Funds'
    @FundLenderId int = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    IF @List = 'Lenders'
        SELECT FundLenderId AS Id, FundLenderName AS [Name], FundLenderId
        FROM   Dim.FundLender
        ORDER BY FundLenderName;
    ELSE
        SELECT FundId AS Id, FundName AS [Name], FundLenderId
        FROM   Dim.Fund
        WHERE  @FundLenderId IS NULL OR FundLenderId = @FundLenderId
        ORDER BY FundName;
END
""");

            // Rpt.spDataStatus
            migrationBuilder.Sql("""
CREATE PROCEDURE Rpt.spDataStatus
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NowUtc datetime2(0) = SYSUTCDATETIME();
    DECLARE @NowUk  datetime2(0) = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE 'GMT Standard Time' AS datetime2(0));
    -- office hours: Monday to Saturday (1 Jan 1900 was a Monday, so day 6 of each week is Sunday), 07:30-20:00
    DECLARE @InOfficeHours bit =
        CASE WHEN DATEDIFF(DAY, '19000101', @NowUk) % 7 <> 6
              AND CAST(@NowUk AS time) BETWEEN '07:30' AND '20:00' THEN 1 ELSE 0 END;

    SELECT
        S.ReportDate,
        S.LastFrequentLoadUtc,
        S.LastNightlyLoadUtc,
        CAST(S.LastFrequentLoadUtc AT TIME ZONE 'UTC' AT TIME ZONE 'GMT Standard Time' AS datetime2(0)) AS LastUpdatedUk,
        CAST(CASE WHEN @InOfficeHours = 1
                   AND (S.LastFrequentLoadUtc IS NULL OR S.LastFrequentLoadUtc < DATEADD(MINUTE, -30, @NowUtc))
                  THEN 1 ELSE 0 END AS bit) AS FiguresStale,
        CAST(CASE WHEN S.LastNightlyLoadUtc IS NULL OR S.LastNightlyLoadUtc < DATEADD(HOUR, -26, @NowUtc)
                  THEN 1 ELSE 0 END AS bit) AS NightlyStale
    FROM   Ref.Setting S
    WHERE  S.SettingId = 1;
END
""");

            // roles (logins and users are added by hand per server)
            migrationBuilder.Sql("""
-- the KPI site: run the reports, read each person's rights and the load log
CREATE ROLE kpi_site;
GRANT EXECUTE ON SCHEMA::Rpt       TO kpi_site;
GRANT SELECT  ON Auth.UserAccess   TO kpi_site;
GRANT SELECT  ON Ref.LoadRun       TO kpi_site;

-- the push program: fill Stage and run the publish; it cannot read the copied data
CREATE ROLE kpi_push;
GRANT SELECT, INSERT, DELETE ON SCHEMA::Stage TO kpi_push;
GRANT EXECUTE ON Stage.spPublish             TO kpi_push;

-- internal CMS.Web: the same reports inside the office
CREATE ROLE kpi_reader;
GRANT EXECUTE ON SCHEMA::Rpt TO kpi_reader;
""");

        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
DROP ROLE IF EXISTS kpi_reader;
DROP ROLE IF EXISTS kpi_push;
DROP ROLE IF EXISTS kpi_site;
DROP PROCEDURE IF EXISTS Rpt.spDataStatus;
DROP PROCEDURE IF EXISTS Rpt.spExposureFilters;
DROP PROCEDURE IF EXISTS Rpt.spExposure;
DROP PROCEDURE IF EXISTS Rpt.spBadDebt;
DROP PROCEDURE IF EXISTS Rpt.spMinIRRAnalysis;
DROP PROCEDURE IF EXISTS Rpt.spMarginAnalysisByYear;
DROP PROCEDURE IF EXISTS Rpt.spMarginAnalysisByMonth;
DROP PROCEDURE IF EXISTS Rpt.spMarginAnalysisByDays;
DROP PROCEDURE IF EXISTS Rpt.spMarginAnalysisByDateRange;
DROP PROCEDURE IF EXISTS Rpt.spHeatMap;
DROP PROCEDURE IF EXISTS Rpt.spSupplierFigures;
DROP PROCEDURE IF EXISTS Rpt.spRecordPerformances;
DROP PROCEDURE IF EXISTS Rpt.spSalesProductivity;
DROP PROCEDURE IF EXISTS Rpt.spSalesTargets;
DROP PROCEDURE IF EXISTS Rpt.spSalesLeagueTimelapse;
DROP PROCEDURE IF EXISTS Rpt.spSalesLeagueTable;
DROP PROCEDURE IF EXISTS Rpt.spBusinessAnalysisByLender;
DROP PROCEDURE IF EXISTS Rpt.spBusinessAnalysis;
DROP PROCEDURE IF EXISTS Rpt.spYearOnYear;
DROP PROCEDURE IF EXISTS Rpt.spSalesSummary;
DROP PROCEDURE IF EXISTS Rpt.spFilterOptions;
DROP FUNCTION IF EXISTS Rpt.fnPeriodDates;
DROP FUNCTION IF EXISTS Rpt.fnReportDate;
DROP PROCEDURE IF EXISTS Stage.spPublish;
DROP TABLE IF EXISTS Stage.SalesOffice;
DROP TABLE IF EXISTS Stage.Salesperson;
DROP TABLE IF EXISTS Stage.Period;
DROP TABLE IF EXISTS Stage.WorkingDay;
DROP TABLE IF EXISTS Stage.SalesTarget;
DROP TABLE IF EXISTS Stage.FundLender;
DROP TABLE IF EXISTS Stage.Fund;
DROP TABLE IF EXISTS Stage.Lead;
DROP TABLE IF EXISTS Stage.LeadPipeline;
DROP TABLE IF EXISTS Stage.SupplierYear;
DROP TABLE IF EXISTS Stage.BookPosition;
DROP TABLE IF EXISTS Stage.FundingLine;
DROP TABLE IF EXISTS Stage.UserAccess;
DROP TABLE IF EXISTS Stage.BadDebtYear;
DROP TABLE IF EXISTS Stage.Exposure;
DROP TABLE IF EXISTS Auth.UserAccess;
DROP TABLE IF EXISTS Fact.Exposure;
DROP TABLE IF EXISTS Fact.BadDebtYear;
DROP TABLE IF EXISTS Fact.FundingLine;
DROP TABLE IF EXISTS Fact.BookPosition;
DROP TABLE IF EXISTS Fact.SupplierYear;
DROP TABLE IF EXISTS Fact.LeadPipeline;
DROP TABLE IF EXISTS Fact.Lead;
DROP TABLE IF EXISTS Dim.Fund;
DROP TABLE IF EXISTS Dim.FundLender;
DROP TABLE IF EXISTS Dim.SalesTarget;
DROP TABLE IF EXISTS Dim.WorkingDay;
DROP TABLE IF EXISTS Dim.Period;
DROP TABLE IF EXISTS Dim.Salesperson;
DROP TABLE IF EXISTS Dim.SalesOffice;
DROP TABLE IF EXISTS Cfg.PostcodeArea;
DROP TABLE IF EXISTS Cfg.SalespersonScope;
DROP TABLE IF EXISTS Ref.LoadRun;
DROP TABLE IF EXISTS Ref.Setting;
DROP SCHEMA IF EXISTS Auth;
DROP SCHEMA IF EXISTS Rpt;
DROP SCHEMA IF EXISTS Fact;
DROP SCHEMA IF EXISTS Dim;
DROP SCHEMA IF EXISTS Stage;
DROP SCHEMA IF EXISTS Cfg;
""");
        }
    }
}
