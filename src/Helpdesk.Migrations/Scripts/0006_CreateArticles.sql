-- Knowledge base articles. The AI answers from these, and agents look them up.

CREATE TABLE dbo.Articles
(
    Id              INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Articles PRIMARY KEY,
    Title           NVARCHAR(300)    NOT NULL,
    Body            NVARCHAR(MAX)    NOT NULL,
    -- Only published articles are found by search.
    IsPublished     BIT              NOT NULL CONSTRAINT DF_Articles_IsPublished DEFAULT 0,
    -- An answer built only from articles with this flag may be sent without an agent reviewing it.
    -- Only an admin can set it.
    AutoSendAllowed BIT              NOT NULL CONSTRAINT DF_Articles_AutoSendAllowed DEFAULT 0,
    CreatedAt       DATETIMEOFFSET   NOT NULL,
    UpdatedAt       DATETIMEOFFSET   NOT NULL,
    UpdatedByUserId UNIQUEIDENTIFIER NULL CONSTRAINT FK_Articles_UpdatedBy REFERENCES dbo.Users (Id)
);
