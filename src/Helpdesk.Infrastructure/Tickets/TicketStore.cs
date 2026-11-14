using System.Data.Common;
using Dapper;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.Tickets;

/// <summary>
/// Reads and writes tickets. Every change runs in one transaction that locks the ticket row,
/// checks the status rules, applies the change, and records a history event.
/// </summary>
public sealed class TicketStore(IDbConnectionFactory connectionFactory, TimeProvider timeProvider)
{
    private const string SummaryColumns = """
        t.Id, t.Subject, t.RequesterEmail, t.RequesterName, t.Status, t.Category, t.AssigneeId,
        assignee.DisplayName AS AssigneeName, t.OriginalTicketId, t.CreatedAt, t.UpdatedAt
        """;

    public async Task<TicketPage> ListAsync(TicketListQuery query, CancellationToken cancellationToken)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (query.Statuses.Count > 0)
        {
            conditions.Add("t.Status IN @statuses");
            parameters.Add("statuses", query.Statuses.Select(status => status.ToString()).ToArray());
        }

        if (query.Unclassified)
        {
            conditions.Add("t.Category IS NULL");
        }
        else if (query.Category is { } category)
        {
            conditions.Add("t.Category = @category");
            parameters.Add("category", category.ToString());
        }

        if (query.Unassigned)
        {
            conditions.Add("t.AssigneeId IS NULL");
        }
        else if (query.AssigneeId is { } assigneeId)
        {
            conditions.Add("t.AssigneeId = @assigneeId");
            parameters.Add("assigneeId", assigneeId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add("""
                (t.Subject LIKE @search ESCAPE '\' OR t.RequesterEmail LIKE @search ESCAPE '\'
                 OR t.RequesterName LIKE @search ESCAPE '\')
                """);
            parameters.Add("search", "%" + EscapeLike(query.Search.Trim()) + "%");
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);

        // The column comes from a fixed list keyed by the enum, never from request text.
        var sortColumn = query.Sort switch
        {
            TicketSort.Id => "t.Id",
            TicketSort.Subject => "t.Subject",
            TicketSort.RequesterEmail => "t.RequesterEmail",
            TicketSort.Status => "t.Status",
            TicketSort.Category => "t.Category",
            TicketSort.UpdatedAt => "t.UpdatedAt",
            _ => "t.CreatedAt",
        };
        var direction = query.Descending ? "DESC" : "ASC";

        parameters.Add("skip", Math.Max(0, query.Skip));
        parameters.Add("take", Math.Clamp(query.Take, 1, TicketListQuery.MaxPageSize));

        var sql = $"""
            SELECT {SummaryColumns}
            FROM dbo.Tickets t
            LEFT JOIN dbo.Users assignee ON assignee.Id = t.AssigneeId
            {where}
            ORDER BY {sortColumn} {direction}, t.Id DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;

            SELECT COUNT(*) FROM dbo.Tickets t {where};
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var results = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        var items = (await results.ReadAsync<TicketSummary>()).AsList();
        var totalCount = await results.ReadSingleAsync<int>();
        return new TicketPage(items, totalCount);
    }

    public async Task<TicketDetail?> GetAsync(int id, CancellationToken cancellationToken)
    {
        const string sql = $"""
            SELECT {SummaryColumns}, t.ResolvedAt, t.ClosedAt
            FROM dbo.Tickets t
            LEFT JOIN dbo.Users assignee ON assignee.Id = t.AssigneeId
            WHERE t.Id = @id;

            SELECT m.Id, m.Direction, m.SenderEmail, m.SenderName, m.AuthorUserId,
                   author.DisplayName AS AuthorName, m.BodyText, m.BodyHtml, m.CreatedAt, m.SendStatus, m.SendError
            FROM dbo.TicketMessages m
            LEFT JOIN dbo.Users author ON author.Id = m.AuthorUserId
            WHERE m.TicketId = @id
            ORDER BY m.Id;

            SELECT e.Id, e.Type, e.ActorUserId, actor.DisplayName AS ActorName, e.FromValue, e.ToValue, e.CreatedAt
            FROM dbo.TicketEvents e
            LEFT JOIN dbo.Users actor ON actor.Id = e.ActorUserId
            WHERE e.TicketId = @id
            ORDER BY e.Id;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var results = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));

        var ticket = await results.ReadSingleOrDefaultAsync<TicketDetail>();
        if (ticket is null)
        {
            return null;
        }

        ticket.Messages = (await results.ReadAsync<TicketMessage>()).AsList();
        ticket.Events = (await results.ReadAsync<TicketEvent>()).AsList();
        return ticket;
    }

    /// <summary>Creates an Open ticket with its first inbound message. Returns the ticket number.</summary>
    public async Task<int> CreateAsync(NewTicket ticket, CancellationToken cancellationToken)
    {
        var receivedAt = ticket.ReceivedAt ?? timeProvider.GetUtcNow();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.Tickets (Subject, RequesterEmail, RequesterName, Status, Category, OriginalTicketId, CreatedAt, UpdatedAt)
            OUTPUT INSERTED.Id
            VALUES (@Subject, @RequesterEmail, @RequesterName, @Status, @Category, @OriginalTicketId, @receivedAt, @receivedAt)
            """,
            new
            {
                ticket.Subject,
                ticket.RequesterEmail,
                ticket.RequesterName,
                Status = TicketStatus.Open.ToString(),
                Category = ticket.Category?.ToString(),
                ticket.OriginalTicketId,
                receivedAt,
            },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TicketMessages
                (TicketId, Direction, SenderEmail, SenderName, BodyText, BodyHtml, CreatedAt,
                 InternetMessageId, ProviderMessageId, ConversationId)
            VALUES
                (@id, @Direction, @RequesterEmail, @RequesterName, @BodyText, @BodyHtml, @receivedAt,
                 @InternetMessageId, @ProviderMessageId, @ConversationId)
            """,
            new
            {
                id,
                Direction = MessageDirection.Inbound.ToString(),
                ticket.RequesterEmail,
                ticket.RequesterName,
                ticket.BodyText,
                ticket.BodyHtml,
                receivedAt,
                ticket.InternetMessageId,
                ticket.ProviderMessageId,
                ticket.ConversationId,
            },
            transaction,
            cancellationToken: cancellationToken));

        await AddEventAsync(connection, transaction, id, TicketEventType.Created, null, null, null, receivedAt, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    /// <summary>An agent changes the status by hand.</summary>
    public async Task<TicketChangeResult> ChangeStatusAsync(
        int id, TicketStatus status, Guid actorId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await LockAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return TicketChangeResult.NotFound;
        }

        if (current.Status == status)
        {
            return TicketChangeResult.Ok;
        }

        if (!TicketStatusRules.IsAllowedForAgent(current.Status, status))
        {
            return TicketChangeResult.NotAllowed;
        }

        var now = timeProvider.GetUtcNow();
        await SetStatusAsync(connection, transaction, id, current.Status, status, actorId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TicketChangeResult.Ok;
    }

    public async Task<TicketChangeResult> ChangeCategoryAsync(
        int id, TicketCategory category, Guid actorId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await LockAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return TicketChangeResult.NotFound;
        }

        if (current.Category == category)
        {
            return TicketChangeResult.Ok;
        }

        if (!TicketStatusRules.AcceptsChanges(current.Status))
        {
            return TicketChangeResult.NotAllowed;
        }

        var now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Tickets SET Category = @category, UpdatedAt = @now WHERE Id = @id",
            new { id, category = category.ToString(), now },
            transaction,
            cancellationToken: cancellationToken));

        await AddEventAsync(
            connection, transaction, id, TicketEventType.CategoryChanged, actorId,
            current.Category?.ToString(), category.ToString(), now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return TicketChangeResult.Ok;
    }

    /// <summary>
    /// Stores an agent's reply on the ticket and, when asked, resolves it. With <paramref name="send"/>
    /// the reply is left pending for the mail sender; otherwise it is marked as stored only.
    /// Returns the new message's id when the reply was added.
    /// </summary>
    public async Task<(TicketChangeResult Result, long? MessageId)> AddAgentReplyAsync(
        int id, Guid authorId, string body, bool resolve, bool send, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await LockAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return (TicketChangeResult.NotFound, null);
        }

        if (!TicketStatusRules.AcceptsChanges(current.Status))
        {
            return (TicketChangeResult.NotAllowed, null);
        }

        var now = timeProvider.GetUtcNow();
        var messageId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO dbo.TicketMessages (TicketId, Direction, AuthorUserId, BodyText, CreatedAt, SendStatus)
            OUTPUT INSERTED.Id
            VALUES (@id, @Direction, @authorId, @body, @now, @SendStatus)
            """,
            new
            {
                id,
                Direction = MessageDirection.Outbound.ToString(),
                authorId,
                body,
                now,
                SendStatus = (send ? MessageSendStatus.Pending : MessageSendStatus.Skipped).ToString(),
            },
            transaction,
            cancellationToken: cancellationToken));

        await AddEventAsync(connection, transaction, id, TicketEventType.ReplyAdded, authorId, null, null, now, cancellationToken);

        var newStatus = TicketStatusRules.AfterAgentReply(current.Status, resolve);
        if (newStatus != current.Status)
        {
            await SetStatusAsync(connection, transaction, id, current.Status, newStatus, authorId, now, cancellationToken);
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.Tickets SET UpdatedAt = @now WHERE Id = @id",
                new { id, now },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return (TicketChangeResult.Ok, messageId);
    }

    /// <summary>
    /// Finds the ticket an incoming email belongs to: one whose messages share its thread id, or
    /// include a message it says it replies to. When a closed ticket and its follow-up share a
    /// thread, the newest ticket wins.
    /// </summary>
    public async Task<int?> FindForEmailAsync(
        string? conversationId, IReadOnlyList<string> referencedMessageIds, CancellationToken cancellationToken)
    {
        if (conversationId is null && referencedMessageIds.Count == 0)
        {
            return null;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT MAX(TicketId)
            FROM dbo.TicketMessages
            WHERE (@conversationId IS NOT NULL AND ConversationId = @conversationId)
               OR InternetMessageId IN @referencedMessageIds
            """,
            // An empty IN list is not valid SQL, so pass a value no Message-ID can equal.
            new { conversationId, referencedMessageIds = referencedMessageIds.Count == 0 ? [""] : referencedMessageIds },
            cancellationToken: cancellationToken));
    }

    /// <summary>Adds the requester's emailed reply to a ticket, reopening it if it was resolved.</summary>
    public async Task<AppendResult> AppendInboundAsync(int id, InboundReply reply, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await LockAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return AppendResult.NotFound;
        }

        if (!TicketStatusRules.AcceptsChanges(current.Status))
        {
            return AppendResult.TicketClosed;
        }

        var now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TicketMessages
                (TicketId, Direction, SenderEmail, SenderName, BodyText, BodyHtml, CreatedAt,
                 InternetMessageId, ProviderMessageId, ConversationId)
            VALUES
                (@id, @Direction, @SenderEmail, @SenderName, @BodyText, @BodyHtml, @ReceivedAt,
                 @InternetMessageId, @ProviderMessageId, @ConversationId)
            """,
            new
            {
                id,
                Direction = MessageDirection.Inbound.ToString(),
                reply.SenderEmail,
                reply.SenderName,
                reply.BodyText,
                reply.BodyHtml,
                reply.ReceivedAt,
                reply.InternetMessageId,
                reply.ProviderMessageId,
                reply.ConversationId,
            },
            transaction,
            cancellationToken: cancellationToken));

        await AddEventAsync(connection, transaction, id, TicketEventType.ReplyReceived, null, null, null, now, cancellationToken);

        var newStatus = TicketStatusRules.AfterRequesterReply(current.Status);
        if (newStatus != current.Status)
        {
            await SetStatusAsync(connection, transaction, id, current.Status, newStatus, null, now, cancellationToken);
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.Tickets SET UpdatedAt = @now WHERE Id = @id",
                new { id, now },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return newStatus != current.Status ? AppendResult.Reopened : AppendResult.Appended;
    }

    /// <summary>Loads an agent reply with what is needed to email it. Null if it is not an outbound message.</summary>
    public async Task<OutboundReply?> GetOutboundAsync(long messageId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<OutboundReply>(new CommandDefinition(
            """
            SELECT m.Id, m.TicketId, t.Subject, t.RequesterEmail, t.RequesterName, m.BodyText, m.SendStatus, m.SendAttempts,
                   (SELECT TOP (1) inbound.ProviderMessageId
                    FROM dbo.TicketMessages inbound
                    WHERE inbound.TicketId = m.TicketId AND inbound.Direction = N'Inbound'
                      AND inbound.ProviderMessageId IS NOT NULL AND inbound.Id < m.Id
                    ORDER BY inbound.Id DESC) AS ReplyToProviderMessageId
            FROM dbo.TicketMessages m
            JOIN dbo.Tickets t ON t.Id = m.TicketId
            WHERE m.Id = @messageId AND m.Direction = N'Outbound'
            """,
            new { messageId },
            cancellationToken: cancellationToken));
    }

    public async Task MarkSentAsync(long messageId, Email.SentEmail sent, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.TicketMessages
            SET SendStatus = N'Sent', SendError = NULL, SendAttempts = SendAttempts + 1, SentAt = @now,
                InternetMessageId = @InternetMessageId, ProviderMessageId = @ProviderMessageId, ConversationId = @ConversationId
            WHERE Id = @messageId
            """,
            new { messageId, now = timeProvider.GetUtcNow(), sent.InternetMessageId, sent.ProviderMessageId, sent.ConversationId },
            cancellationToken: cancellationToken));
    }

    /// <summary>Counts a failed attempt. When <paramref name="final"/>, no more attempts will be made automatically.</summary>
    public async Task RecordSendFailureAsync(long messageId, string error, bool final, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.TicketMessages
            SET SendAttempts = SendAttempts + 1, SendError = @error, SendStatus = @status
            WHERE Id = @messageId
            """,
            new
            {
                messageId,
                error = error.Length <= 1000 ? error : error[..1000],
                status = (final ? MessageSendStatus.Failed : MessageSendStatus.Pending).ToString(),
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>Puts a failed reply back in line to be sent. False if the message is not a failed reply on this ticket.</summary>
    public async Task<bool> RetrySendAsync(int ticketId, long messageId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.TicketMessages
            SET SendStatus = N'Pending', SendAttempts = 0, SendError = NULL
            WHERE Id = @messageId AND TicketId = @ticketId AND SendStatus = N'Failed'
            """,
            new { ticketId, messageId },
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Tickets) THEN 1 ELSE 0 END",
            cancellationToken: cancellationToken));
    }

    /// <summary>Reads the ticket's state and holds an update lock on its row until the transaction ends.</summary>
    private static Task<LockedTicket?> LockAsync(
        DbConnection connection, DbTransaction transaction, int id, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<LockedTicket>(new CommandDefinition(
            "SELECT Status, Category FROM dbo.Tickets WITH (UPDLOCK, ROWLOCK) WHERE Id = @id",
            new { id },
            transaction,
            cancellationToken: cancellationToken));

    private static async Task SetStatusAsync(
        DbConnection connection,
        DbTransaction transaction,
        int id,
        TicketStatus from,
        TicketStatus to,
        Guid? actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // ResolvedAt and ClosedAt hold the time the ticket entered that status, and are cleared on reopening.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Tickets
            SET Status = @status,
                UpdatedAt = @now,
                ResolvedAt = CASE @status WHEN N'Resolved' THEN @now WHEN N'Closed' THEN ResolvedAt ELSE NULL END,
                ClosedAt = CASE @status WHEN N'Closed' THEN @now ELSE NULL END
            WHERE Id = @id
            """,
            new { id, status = to.ToString(), now },
            transaction,
            cancellationToken: cancellationToken));

        await AddEventAsync(
            connection, transaction, id, TicketEventType.StatusChanged, actorId,
            from.ToString(), to.ToString(), now, cancellationToken);
    }

    private static Task AddEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        int ticketId,
        TicketEventType type,
        Guid? actorId,
        string? from,
        string? to,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TicketEvents (TicketId, Type, ActorUserId, FromValue, ToValue, CreatedAt)
            VALUES (@ticketId, @Type, @actorId, @from, @to, @now)
            """,
            new { ticketId, Type = type.ToString(), actorId, from, to, now },
            transaction,
            cancellationToken: cancellationToken));

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");

    private sealed class LockedTicket
    {
        public TicketStatus Status { get; init; }
        public TicketCategory? Category { get; init; }
    }
}
