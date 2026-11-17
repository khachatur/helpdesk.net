-- Sign-in sessions live in the database. The browser's cookie carries only a random session id;
-- this table holds a SHA-256 hash of that id, so a copy of the table cannot be used to sign in.

CREATE TABLE dbo.UserSessions
(
    KeyHash   BINARY(32)       NOT NULL CONSTRAINT PK_UserSessions PRIMARY KEY,
    UserId    UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT FK_UserSessions_User REFERENCES dbo.Users (Id) ON DELETE CASCADE,
    -- The serialized authentication ticket: the user's id, name, role and security stamp.
    Ticket    VARBINARY(MAX)   NOT NULL,
    CreatedAt DATETIMEOFFSET   NOT NULL,
    -- Moves forward while the session is in use (sliding expiration).
    ExpiresAt DATETIMEOFFSET   NOT NULL
);

CREATE INDEX IX_UserSessions_UserId ON dbo.UserSessions (UserId, CreatedAt);
CREATE INDEX IX_UserSessions_ExpiresAt ON dbo.UserSessions (ExpiresAt);
