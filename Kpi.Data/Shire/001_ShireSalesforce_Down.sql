-- Undoes 001_ShireSalesforce.sql (migration ShireSalesforce, Down): every Shire object, then the schema.
DROP PROCEDURE IF EXISTS Shire.spSalesLeague;
DROP PROCEDURE IF EXISTS Shire.spYearOnYear;
DROP PROCEDURE IF EXISTS Shire.spSalespeople;
DROP PROCEDURE IF EXISTS Shire.spLoadHistory;
DROP PROCEDURE IF EXISTS Shire.spDataStatus;
DROP PROCEDURE IF EXISTS Shire.spChecked;
DROP PROCEDURE IF EXISTS Shire.spRecordFailure;
DROP PROCEDURE IF EXISTS Shire.spPublish;
DROP FUNCTION IF EXISTS Shire.fnReportDate;
DROP TABLE IF EXISTS Shire.LoadRun;
DROP TABLE IF EXISTS Shire.Setting;
DROP TABLE IF EXISTS Shire.Sale;
DROP TABLE IF EXISTS Shire.StageSale;
GO
IF SCHEMA_ID(N'Shire') IS NOT NULL EXEC(N'DROP SCHEMA Shire');
