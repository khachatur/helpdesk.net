-- AI-drafted replies and auto-send.

CREATE TABLE dbo.TicketDrafts
(
    Id                  BIGINT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_TicketDrafts PRIMARY KEY,
    TicketId            INT              NOT NULL CONSTRAINT FK_TicketDrafts_Ticket REFERENCES dbo.Tickets (Id),
    Body                NVARCHAR(MAX)    NOT NULL,
    -- The AI's own estimate, from 0 to 1, that an agent would send this reply as written.
    Confidence          DECIMAL(4, 3)    NOT NULL,
    -- Pending: waiting for an agent. Sent: an agent sent it unchanged. Edited: an agent sent it
    -- with changes. Discarded: an agent rejected it or wrote their own reply. Superseded: the
    -- conversation moved on and a newer draft replaced it. AutoSent: sent without review.
    Status              NVARCHAR(20)     NOT NULL
        CONSTRAINT CK_TicketDrafts_Status CHECK (Status IN
            (N'Pending', N'Sent', N'Edited', N'Discarded', N'Superseded', N'AutoSent')),
    -- What the auto-send check decided. WouldSend is shadow mode: it passed, and nothing was sent.
    GateDecision        NVARCHAR(20)     NOT NULL
        CONSTRAINT CK_TicketDrafts_GateDecision CHECK (GateDecision IN (N'Hold', N'WouldSend', N'Send')),
    -- Why it was held, one reason per line.
    GateReasons         NVARCHAR(2000)   NULL,
    -- How many messages the ticket had when this was drafted. More messages means it is out of date.
    BasedOnMessageCount INT              NOT NULL,
    CreatedAt           DATETIMEOFFSET   NOT NULL,
    ResolvedAt          DATETIMEOFFSET   NULL,
    ResolvedByUserId    UNIQUEIDENTIFIER NULL CONSTRAINT FK_TicketDrafts_ResolvedBy REFERENCES dbo.Users (Id),
    SentMessageId       BIGINT           NULL CONSTRAINT FK_TicketDrafts_Message REFERENCES dbo.TicketMessages (Id)
);

CREATE INDEX IX_TicketDrafts_TicketId ON dbo.TicketDrafts (TicketId, Id);
CREATE INDEX IX_TicketDrafts_CreatedAt ON dbo.TicketDrafts (CreatedAt);

-- The knowledge base articles a draft was written from. The auto-send check reads these.
CREATE TABLE dbo.TicketDraftSources
(
    DraftId   BIGINT NOT NULL CONSTRAINT FK_TicketDraftSources_Draft REFERENCES dbo.TicketDrafts (Id),
    ArticleId INT    NOT NULL CONSTRAINT FK_TicketDraftSources_Article REFERENCES dbo.Articles (Id),
    CONSTRAINT PK_TicketDraftSources PRIMARY KEY (DraftId, ArticleId)
);

ALTER TABLE dbo.Tickets ADD
    -- Set once the requester replies to an AI-sent answer. From then on a person handles the ticket.
    AutoSendDisabled BIT NOT NULL CONSTRAINT DF_Tickets_AutoSendDisabled DEFAULT 0;

ALTER TABLE dbo.TicketMessages ADD
    -- An outbound message written by the AI and sent without an agent reviewing it.
    IsAiGenerated BIT NOT NULL CONSTRAINT DF_TicketMessages_IsAiGenerated DEFAULT 0;

ALTER TABLE dbo.TicketEvents DROP CONSTRAINT CK_TicketEvents_Type;
ALTER TABLE dbo.TicketEvents ADD CONSTRAINT CK_TicketEvents_Type
    CHECK (Type IN (N'Created', N'StatusChanged', N'CategoryChanged', N'ReplyAdded', N'ReplyReceived',
                    N'TeamChanged', N'Assigned', N'AiReplySent', N'Escalated'));
