-- Users sign in with their email address. Each user has exactly one role, so the role is a column
-- rather than separate role tables.

CREATE TABLE dbo.Users
(
    Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Email             NVARCHAR(256)    NOT NULL,
    NormalizedEmail   NVARCHAR(256)    NOT NULL CONSTRAINT UQ_Users_NormalizedEmail UNIQUE,
    DisplayName       NVARCHAR(200)    NOT NULL,
    PasswordHash      NVARCHAR(MAX)    NULL,
    SecurityStamp     NVARCHAR(100)    NOT NULL,
    Role              NVARCHAR(20)     NOT NULL CONSTRAINT CK_Users_Role CHECK (Role IN (N'Admin', N'Agent')),
    IsActive          BIT              NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1,
    AccessFailedCount INT              NOT NULL CONSTRAINT DF_Users_AccessFailedCount DEFAULT 0,
    LockoutEnd        DATETIMEOFFSET   NULL,
    CreatedAt         DATETIMEOFFSET   NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSDATETIMEOFFSET()
);
