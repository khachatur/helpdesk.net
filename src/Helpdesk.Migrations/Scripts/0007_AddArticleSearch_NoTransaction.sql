-- Full-text search over articles. SQL Server does not allow a full-text catalog or index to be
-- created inside a transaction, so this script's name ends in _NoTransaction, which makes the
-- migrator run it without one. Each statement checks first, so a half-finished run can be repeated.

IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') <> 1
    THROW 50000, 'Full-Text Search is not installed on this SQL Server instance. Add the feature (SQL Server setup, or the mssql-server-fts package on Linux) and start the application again.', 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'HelpdeskCatalog')
    CREATE FULLTEXT CATALOG HelpdeskCatalog;
GO

IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Articles'))
    CREATE FULLTEXT INDEX ON dbo.Articles (Title LANGUAGE 1033, Body LANGUAGE 1033)
        KEY INDEX PK_Articles ON HelpdeskCatalog
        WITH CHANGE_TRACKING AUTO;
GO
