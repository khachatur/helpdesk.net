using Dapper;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.KnowledgeBase;

public sealed class Article
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsPublished { get; init; }

    /// <summary>An answer built only from articles with this flag may be sent without review.</summary>
    public bool AutoSendAllowed { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? UpdatedByName { get; init; }
}

/// <summary>A published article that matched a search, best match first.</summary>
public sealed class ArticleMatch
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public bool AutoSendAllowed { get; init; }

    /// <summary>Higher is a better match. Only meaningful for comparing results of one search.</summary>
    public int Score { get; init; }
}

public sealed record ArticleContent(string Title, string Body, bool IsPublished, bool AutoSendAllowed);

/// <summary>Knowledge base articles and the full-text search over them.</summary>
public sealed class ArticleStore(IDbConnectionFactory connectionFactory, TimeProvider timeProvider)
{
    /// <summary>The longest question text passed to the search engine. An email is cut to its opening.</summary>
    public const int MaxQuestionLength = 2000;

    /// <summary>A match in the title counts this many times a match in the body.</summary>
    private const int TitleWeight = 3;

    private const string Columns = """
        a.Id, a.Title, a.Body, a.IsPublished, a.AutoSendAllowed, a.CreatedAt, a.UpdatedAt,
        editor.DisplayName AS UpdatedByName
        """;

    public async Task<IReadOnlyList<Article>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var articles = await connection.QueryAsync<Article>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM dbo.Articles a
            LEFT JOIN dbo.Users editor ON editor.Id = a.UpdatedByUserId
            ORDER BY a.Title, a.Id
            """,
            cancellationToken: cancellationToken));
        return articles.AsList();
    }

    public async Task<Article?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Article>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM dbo.Articles a
            LEFT JOIN dbo.Users editor ON editor.Id = a.UpdatedByUserId
            WHERE a.Id = @id
            """,
            new { id },
            cancellationToken: cancellationToken));
    }

    public async Task<int> CreateAsync(ArticleContent content, Guid? userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.Articles (Title, Body, IsPublished, AutoSendAllowed, CreatedAt, UpdatedAt, UpdatedByUserId)
            OUTPUT INSERTED.Id
            VALUES (@Title, @Body, @IsPublished, @AutoSendAllowed, @now, @now, @userId)
            """,
            new { content.Title, content.Body, content.IsPublished, content.AutoSendAllowed, now = timeProvider.GetUtcNow(), userId },
            cancellationToken: cancellationToken));
    }

    /// <summary>Replaces the article's content. False if the article does not exist.</summary>
    public async Task<bool> UpdateAsync(int id, ArticleContent content, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Articles
            SET Title = @Title, Body = @Body, IsPublished = @IsPublished, AutoSendAllowed = @AutoSendAllowed,
                UpdatedAt = @now, UpdatedByUserId = @userId
            WHERE Id = @id
            """,
            new { id, content.Title, content.Body, content.IsPublished, content.AutoSendAllowed, now = timeProvider.GetUtcNow(), userId },
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Articles) THEN 1 ELSE 0 END",
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Finds the published articles that best answer a question written in ordinary language,
    /// best first. Words are matched in any of their forms ("resetting" finds "reset"), and common
    /// words are ignored. Returns nothing for a question with no searchable words.
    /// </summary>
    public async Task<IReadOnlyList<ArticleMatch>> SearchAsync(string question, int take, CancellationToken cancellationToken)
    {
        var text = question.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        if (text.Length > MaxQuestionLength)
        {
            text = text[..MaxQuestionLength];
        }

        // FREETEXTTABLE takes the question as plain text, so nothing in it is read as search syntax.
        var sql = $"""
            SELECT TOP (@take) a.Id, a.Title, a.Body, a.AutoSendAllowed,
                   COALESCE(title.[RANK], 0) * {TitleWeight} + COALESCE(body.[RANK], 0) AS Score
            FROM dbo.Articles a
            LEFT JOIN FREETEXTTABLE(dbo.Articles, Title, @text, LANGUAGE 1033) title ON title.[KEY] = a.Id
            LEFT JOIN FREETEXTTABLE(dbo.Articles, Body, @text, LANGUAGE 1033) body ON body.[KEY] = a.Id
            WHERE a.IsPublished = 1 AND (title.[KEY] IS NOT NULL OR body.[KEY] IS NOT NULL)
            ORDER BY Score DESC, a.Id
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var matches = await connection.QueryAsync<ArticleMatch>(new CommandDefinition(
            sql, new { text, take = Math.Clamp(take, 1, 20) }, cancellationToken: cancellationToken));
        return matches.AsList();
    }

    /// <summary>
    /// Waits until the search index has caught up with recent article changes. The index is
    /// updated in the background a moment after a change, so tests and seeding call this before searching.
    /// </summary>
    public async Task WaitForIndexAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                -- "Nothing pending" alone can be true for an instant before indexing of a fresh change
                -- begins, so the number of indexed rows must also equal the number of articles.
                SELECT CASE WHEN FULLTEXTCATALOGPROPERTY(N'HelpdeskCatalog', 'PopulateStatus') = 0
                             AND OBJECTPROPERTYEX(OBJECT_ID(N'dbo.Articles'), 'TableFulltextPendingChanges') = 0
                             AND OBJECTPROPERTYEX(OBJECT_ID(N'dbo.Articles'), 'TableFulltextPopulateStatus') = 0
                             AND CONVERT(INT, OBJECTPROPERTYEX(OBJECT_ID(N'dbo.Articles'), 'TableFulltextItemCount'))
                                 = (SELECT COUNT(*) FROM dbo.Articles)
                            THEN 0 ELSE 1 END
                """,
                cancellationToken: cancellationToken));

            if (pending == 0)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("The article search index did not finish updating within 15 seconds.");
    }
}
