-- Teams, routing of categories to teams, and ticket ownership within a team.

CREATE TABLE dbo.Teams
(
    Id                 INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Teams PRIMARY KEY,
    Name               NVARCHAR(100)    NOT NULL CONSTRAINT UQ_Teams_Name UNIQUE,
    -- The default team takes tickets that have no category, or whose category has no route.
    IsDefault          BIT              NOT NULL CONSTRAINT DF_Teams_IsDefault DEFAULT 0,
    -- Round-robin position: the member who was given this team's most recent ticket.
    LastAssignedUserId UNIQUEIDENTIFIER NULL CONSTRAINT FK_Teams_LastAssigned REFERENCES dbo.Users (Id),
    CreatedAt          DATETIMEOFFSET   NOT NULL CONSTRAINT DF_Teams_CreatedAt DEFAULT SYSDATETIMEOFFSET()
);

-- At most one default team.
CREATE UNIQUE INDEX UX_Teams_Default ON dbo.Teams (IsDefault) WHERE IsDefault = 1;

-- Which team handles each category. A category with no row goes to the default team.
CREATE TABLE dbo.CategoryRoutes
(
    Category NVARCHAR(30) NOT NULL CONSTRAINT PK_CategoryRoutes PRIMARY KEY
        CONSTRAINT CK_CategoryRoutes_Category CHECK (Category IN (N'GeneralQuestion', N'TechnicalQuestion', N'RefundRequest')),
    TeamId   INT          NOT NULL CONSTRAINT FK_CategoryRoutes_Team REFERENCES dbo.Teams (Id)
);

INSERT INTO dbo.Teams (Name, IsDefault)
VALUES (N'General Support', 1), (N'Technical Support', 0), (N'Finance Support', 0);

INSERT INTO dbo.CategoryRoutes (Category, TeamId)
SELECT route.Category, team.Id
FROM (VALUES (N'GeneralQuestion', N'General Support'),
             (N'TechnicalQuestion', N'Technical Support'),
             (N'RefundRequest', N'Finance Support')) AS route (Category, TeamName)
JOIN dbo.Teams team ON team.Name = route.TeamName;

-- A user belongs to at most one team.
ALTER TABLE dbo.Users ADD TeamId INT NULL CONSTRAINT FK_Users_Team REFERENCES dbo.Teams (Id);
ALTER TABLE dbo.Tickets ADD TeamId INT NULL CONSTRAINT FK_Tickets_Team REFERENCES dbo.Teams (Id);
GO

CREATE INDEX IX_Users_TeamId ON dbo.Users (TeamId) WHERE TeamId IS NOT NULL;
CREATE INDEX IX_Tickets_TeamId ON dbo.Tickets (TeamId, Status);

-- Put existing tickets in the queue their category routes to.
UPDATE ticket
SET TeamId = COALESCE(route.TeamId, (SELECT Id FROM dbo.Teams WHERE IsDefault = 1))
FROM dbo.Tickets ticket
LEFT JOIN dbo.CategoryRoutes route ON route.Category = ticket.Category;

ALTER TABLE dbo.TicketEvents DROP CONSTRAINT CK_TicketEvents_Type;
ALTER TABLE dbo.TicketEvents ADD CONSTRAINT CK_TicketEvents_Type
    CHECK (Type IN (N'Created', N'StatusChanged', N'CategoryChanged', N'ReplyAdded', N'ReplyReceived',
                    N'TeamChanged', N'Assigned'));
