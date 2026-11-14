-- AI classification and summaries.

ALTER TABLE dbo.Tickets ADD
    -- NULL: classification was never requested (AI off, or the ticket came with a category).
    -- Pending: waiting for the AI; the ticket is not in a team queue yet.
    -- Done: the AI answered. Failed: it could not, and the ticket went to the default team.
    ClassificationStatus      NVARCHAR(20)   NULL
        CONSTRAINT CK_Tickets_ClassificationStatus CHECK (ClassificationStatus IN (N'Pending', N'Done', N'Failed')),
    ClassificationRequestedAt DATETIMEOFFSET NULL,
    ClassificationAttempts    INT            NOT NULL CONSTRAINT DF_Tickets_ClassificationAttempts DEFAULT 0,
    -- What the AI said, kept apart from Category so that an agent's correction does not erase it.
    -- Comparing the two is how classification accuracy is measured.
    AiCategory                NVARCHAR(30)   NULL
        CONSTRAINT CK_Tickets_AiCategory CHECK (AiCategory IN (N'GeneralQuestion', N'TechnicalQuestion', N'RefundRequest')),
    AiConfidence              DECIMAL(4, 3)  NULL,
    Summary                   NVARCHAR(MAX)  NULL,
    -- How many messages the summary covers. A higher message count means the summary is out of date.
    SummaryMessageCount       INT            NULL,
    SummaryUpdatedAt          DATETIMEOFFSET NULL;
GO

CREATE INDEX IX_Tickets_ClassificationPending
    ON dbo.Tickets (ClassificationRequestedAt) WHERE ClassificationStatus = N'Pending';

-- One row per call to the AI provider, successful or not.
CREATE TABLE dbo.AiRuns
(
    Id           BIGINT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_AiRuns PRIMARY KEY,
    TicketId     INT            NULL CONSTRAINT FK_AiRuns_Ticket REFERENCES dbo.Tickets (Id),
    Purpose      NVARCHAR(30)   NOT NULL,
    Model        NVARCHAR(100)  NOT NULL,
    InputTokens  INT            NULL,
    OutputTokens INT            NULL,
    LatencyMs    INT            NOT NULL,
    -- Succeeded, or why not: Failed (the call errored), Refused (the model declined),
    -- Invalid (the answer did not pass validation).
    Outcome      NVARCHAR(20)   NOT NULL
        CONSTRAINT CK_AiRuns_Outcome CHECK (Outcome IN (N'Succeeded', N'Failed', N'Refused', N'Invalid')),
    Error        NVARCHAR(1000) NULL,
    CreatedAt    DATETIMEOFFSET NOT NULL
);

CREATE INDEX IX_AiRuns_TicketId ON dbo.AiRuns (TicketId, Id);
CREATE INDEX IX_AiRuns_CreatedAt ON dbo.AiRuns (CreatedAt);
