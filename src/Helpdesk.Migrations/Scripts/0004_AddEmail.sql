-- Email integration: link messages to the emails they came from or were sent as,
-- track sending of agent replies, and remember what has been read from the mailbox.

ALTER TABLE dbo.TicketMessages ADD
    -- The RFC 5322 Message-ID. Replies name it in their In-Reply-To and References headers.
    InternetMessageId NVARCHAR(450)  NULL,
    -- The mail provider's own id for the message, needed to reply to it.
    ProviderMessageId NVARCHAR(450)  NULL,
    -- The provider's thread id. Every message in one email thread shares it.
    ConversationId    NVARCHAR(450)  NULL,
    -- Outbound only. Pending: waiting to be sent. Skipped: email was not configured.
    SendStatus        NVARCHAR(20)   NULL
        CONSTRAINT CK_TicketMessages_SendStatus CHECK (SendStatus IN (N'Pending', N'Sent', N'Failed', N'Skipped')),
    SendAttempts      INT            NOT NULL CONSTRAINT DF_TicketMessages_SendAttempts DEFAULT 0,
    SendError         NVARCHAR(1000) NULL,
    SentAt            DATETIMEOFFSET NULL;
GO

-- Replies stored before email integration existed were never sent.
UPDATE dbo.TicketMessages SET SendStatus = N'Skipped' WHERE Direction = N'Outbound';

CREATE UNIQUE INDEX UX_TicketMessages_InternetMessageId
    ON dbo.TicketMessages (InternetMessageId) WHERE InternetMessageId IS NOT NULL;
CREATE INDEX IX_TicketMessages_ConversationId
    ON dbo.TicketMessages (ConversationId) WHERE ConversationId IS NOT NULL;

ALTER TABLE dbo.TicketEvents DROP CONSTRAINT CK_TicketEvents_Type;
ALTER TABLE dbo.TicketEvents ADD CONSTRAINT CK_TicketEvents_Type
    CHECK (Type IN (N'Created', N'StatusChanged', N'CategoryChanged', N'ReplyAdded', N'ReplyReceived'));

-- One row per email read from the mailbox, whatever was done with it. The unique key makes
-- ingestion idempotent: an email already listed here is never processed twice.
CREATE TABLE dbo.InboundEmails
(
    Id                BIGINT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_InboundEmails PRIMARY KEY,
    InternetMessageId NVARCHAR(450)  NOT NULL CONSTRAINT UQ_InboundEmails_InternetMessageId UNIQUE,
    FromEmail         NVARCHAR(320)  NOT NULL,
    Subject           NVARCHAR(500)  NOT NULL,
    ReceivedAt        DATETIMEOFFSET NOT NULL,
    Outcome           NVARCHAR(30)   NOT NULL
        CONSTRAINT CK_InboundEmails_Outcome CHECK (Outcome IN
            (N'CreatedTicket', N'AppendedToTicket', N'ReopenedTicket', N'CreatedFollowUpTicket',
             N'SkippedAutomated', N'SkippedOwnMessage', N'Failed')),
    TicketId          INT            NULL CONSTRAINT FK_InboundEmails_Ticket REFERENCES dbo.Tickets (Id),
    Error             NVARCHAR(1000) NULL,
    ProcessedAt       DATETIMEOFFSET NOT NULL
);

-- Where reading of each mailbox has got to.
CREATE TABLE dbo.MailboxState
(
    Mailbox    NVARCHAR(320)  NOT NULL CONSTRAINT PK_MailboxState PRIMARY KEY,
    -- Opaque position returned by the mail provider. NULL until the first read completes.
    [Cursor]   NVARCHAR(MAX)  NULL,
    -- Mail received before this moment is never turned into tickets.
    IngestFrom DATETIMEOFFSET NOT NULL,
    UpdatedAt  DATETIMEOFFSET NOT NULL
);
