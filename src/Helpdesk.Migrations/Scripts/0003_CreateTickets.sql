-- Tickets, their conversation, and a history of what happened to them.
-- The team column arrives with the teams table (phase 4).

CREATE TABLE dbo.Tickets
(
    -- Ticket numbers are shown to people, so they are short integers starting at 1000.
    Id               INT IDENTITY (1000, 1) NOT NULL CONSTRAINT PK_Tickets PRIMARY KEY,
    Subject          NVARCHAR(500)    NOT NULL,
    RequesterEmail   NVARCHAR(320)    NOT NULL,
    RequesterName    NVARCHAR(200)    NULL,
    Status           NVARCHAR(30)     NOT NULL
        CONSTRAINT CK_Tickets_Status CHECK (Status IN (N'Open', N'NeedsHumanReview', N'Resolved', N'Closed')),
    -- NULL means the ticket has not been classified yet.
    Category         NVARCHAR(30)     NULL
        CONSTRAINT CK_Tickets_Category CHECK (Category IN (N'GeneralQuestion', N'TechnicalQuestion', N'RefundRequest')),
    AssigneeId       UNIQUEIDENTIFIER NULL CONSTRAINT FK_Tickets_Assignee REFERENCES dbo.Users (Id),
    -- Set when a reply to a closed ticket created this one.
    OriginalTicketId INT              NULL CONSTRAINT FK_Tickets_Original REFERENCES dbo.Tickets (Id),
    CreatedAt        DATETIMEOFFSET   NOT NULL,
    UpdatedAt        DATETIMEOFFSET   NOT NULL,
    ResolvedAt       DATETIMEOFFSET   NULL,
    ClosedAt         DATETIMEOFFSET   NULL
);

CREATE INDEX IX_Tickets_Status_CreatedAt ON dbo.Tickets (Status, CreatedAt DESC);
CREATE INDEX IX_Tickets_AssigneeId ON dbo.Tickets (AssigneeId) WHERE AssigneeId IS NOT NULL;
CREATE INDEX IX_Tickets_RequesterEmail ON dbo.Tickets (RequesterEmail);

CREATE TABLE dbo.TicketMessages
(
    Id           BIGINT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_TicketMessages PRIMARY KEY,
    TicketId     INT              NOT NULL CONSTRAINT FK_TicketMessages_Ticket REFERENCES dbo.Tickets (Id),
    -- Inbound: from the requester. Outbound: from an agent (or, later, the AI).
    Direction    NVARCHAR(10)     NOT NULL
        CONSTRAINT CK_TicketMessages_Direction CHECK (Direction IN (N'Inbound', N'Outbound')),
    SenderEmail  NVARCHAR(320)    NULL,
    SenderName   NVARCHAR(200)    NULL,
    AuthorUserId UNIQUEIDENTIFIER NULL CONSTRAINT FK_TicketMessages_Author REFERENCES dbo.Users (Id),
    BodyText     NVARCHAR(MAX)    NOT NULL,
    -- The original email HTML, stored as received. It is untrusted and is sanitised when read.
    BodyHtml     NVARCHAR(MAX)    NULL,
    CreatedAt    DATETIMEOFFSET   NOT NULL
);

CREATE INDEX IX_TicketMessages_TicketId ON dbo.TicketMessages (TicketId, Id);

CREATE TABLE dbo.TicketEvents
(
    Id          BIGINT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_TicketEvents PRIMARY KEY,
    TicketId    INT              NOT NULL CONSTRAINT FK_TicketEvents_Ticket REFERENCES dbo.Tickets (Id),
    Type        NVARCHAR(30)     NOT NULL
        CONSTRAINT CK_TicketEvents_Type CHECK (Type IN (N'Created', N'StatusChanged', N'CategoryChanged', N'ReplyAdded')),
    -- NULL means the system did it.
    ActorUserId UNIQUEIDENTIFIER NULL CONSTRAINT FK_TicketEvents_Actor REFERENCES dbo.Users (Id),
    FromValue   NVARCHAR(100)    NULL,
    ToValue     NVARCHAR(100)    NULL,
    CreatedAt   DATETIMEOFFSET   NOT NULL
);

CREATE INDEX IX_TicketEvents_TicketId ON dbo.TicketEvents (TicketId, Id);
