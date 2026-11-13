-- First migration. Creates nothing yet; it proves the migration pipeline runs.
-- Add new scripts as 0002_<Name>.sql, 0003_<Name>.sql, ... Never edit a script that has been applied.

IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') <> 1
    PRINT 'WARNING: Full-Text Search is not installed on this SQL Server instance. Knowledge base search will need it.';
ELSE
    PRINT 'Full-Text Search is installed.';
