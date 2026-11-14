using System.Net;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.MailFolders.Item.Messages.Delta;
using Microsoft.Graph.Users.Item.Messages.Item.CreateReply;

namespace Helpdesk.Infrastructure.Email;

/// <summary>
/// The shared mailbox through Microsoft Graph, signed in as the application (client credentials).
/// The app registration needs the Mail.ReadWrite and Mail.Send application permissions, and an
/// application access policy in Exchange Online restricting it to this one mailbox.
/// </summary>
public sealed class GraphMailbox : IMailbox
{
    private static readonly string[] MessageFields =
    [
        "id", "internetMessageId", "conversationId", "subject", "from", "receivedDateTime", "body",
        "internetMessageHeaders", "isDraft",
    ];

    private readonly GraphServiceClient _graph;
    private readonly string _mailbox;

    public GraphMailbox(IOptions<EmailOptions> options)
    {
        var settings = options.Value;
        _mailbox = settings.Mailbox;
        _graph = new GraphServiceClient(
            new ClientSecretCredential(settings.TenantId, settings.ClientId, settings.ClientSecret),
            ["https://graph.microsoft.com/.default"]);
    }

    public async Task<MailboxChanges> GetNewMessagesAsync(string? cursor, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var inbox = _graph.Users[_mailbox].MailFolders["inbox"].Messages.Delta;

        // The delta query lists which messages changed. It is asked for ids only; each new
        // message is then fetched whole, because internet headers are not reliably part of a delta page.
        var page = cursor is null
            ? await inbox.GetAsDeltaGetResponseAsync(
                request =>
                {
                    request.QueryParameters.Select = ["id"];
                    request.QueryParameters.Filter = $"receivedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
                },
                cancellationToken)
            : await new DeltaRequestBuilder(cursor, _graph.RequestAdapter)
                .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);

        var ids = new List<string>();
        while (page is not null)
        {
            foreach (var item in page.Value ?? [])
            {
                // "@removed" marks a message that was deleted or moved out of the inbox.
                if (item.Id is not null && !item.AdditionalData.ContainsKey("@removed"))
                {
                    ids.Add(item.Id);
                }
            }

            if (page.OdataNextLink is null)
            {
                break;
            }

            page = await new DeltaRequestBuilder(page.OdataNextLink, _graph.RequestAdapter)
                .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);
        }

        var messages = new List<InboundEmail>();
        foreach (var id in ids.Distinct())
        {
            var message = await GetMessageAsync(id, cancellationToken);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        // Keep the old cursor if Graph returned no delta link, so nothing is skipped.
        return new MailboxChanges(messages, page?.OdataDeltaLink ?? cursor);
    }

    private async Task<InboundEmail?> GetMessageAsync(string id, CancellationToken cancellationToken)
    {
        Message? message;
        try
        {
            message = await _graph.Users[_mailbox].Messages[id].GetAsync(
                request => request.QueryParameters.Select = MessageFields, cancellationToken);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError error) when (error.ResponseStatusCode == (int)HttpStatusCode.NotFound)
        {
            // Deleted or moved between the delta query and this read.
            return null;
        }

        var from = message?.From?.EmailAddress?.Address;
        if (message?.Id is null || message.InternetMessageId is null || from is null || message.IsDraft == true)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.InternetMessageHeaders ?? [])
        {
            if (header.Name is not null && header.Value is not null)
            {
                headers[header.Name] = header.Value;
            }
        }

        var isHtml = message.Body?.ContentType == BodyType.Html;
        return new InboundEmail(
            message.Id,
            message.InternetMessageId,
            message.ConversationId,
            from,
            message.From?.EmailAddress?.Name,
            message.Subject ?? "",
            isHtml ? message.Body?.Content : null,
            isHtml ? null : message.Body?.Content,
            message.ReceivedDateTime ?? DateTimeOffset.UtcNow,
            headers);
    }

    public async Task<SentEmail> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        var messages = _graph.Users[_mailbox].Messages;
        var html = "<div>" + WebUtility.HtmlEncode(email.BodyText).Replace("\r\n", "\n").Replace("\n", "<br>") + "</div>";

        Message? draft;
        if (email.ReplyToProviderMessageId is { } replyTo)
        {
            // A reply draft carries the thread headers and the quoted original. Our text goes above the quote.
            draft = await messages[replyTo].CreateReply.PostAsync(new CreateReplyPostRequestBody(), cancellationToken: cancellationToken);
            var quoted = draft?.Body?.Content ?? "";
            draft = await messages[draft!.Id].PatchAsync(
                new Message { Body = new ItemBody { ContentType = BodyType.Html, Content = html + "<br>" + quoted } },
                cancellationToken: cancellationToken);
        }
        else
        {
            draft = await messages.PostAsync(
                new Message
                {
                    Subject = email.Subject,
                    Body = new ItemBody { ContentType = BodyType.Html, Content = html },
                    ToRecipients =
                    [
                        new Recipient { EmailAddress = new EmailAddress { Address = email.ToEmail, Name = email.ToName } },
                    ],
                },
                cancellationToken: cancellationToken);
        }

        await messages[draft!.Id].Send.PostAsync(cancellationToken: cancellationToken);

        // The draft already has the Message-ID it will be sent with, which is what the requester's reply will reference.
        return new SentEmail(draft.Id, draft.InternetMessageId, draft.ConversationId);
    }
}

/// <summary>Stands in when no mailbox is configured. Nothing should call it; if something does, it says why it failed.</summary>
public sealed class NoMailbox : IMailbox
{
    public Task<MailboxChanges> GetNewMessagesAsync(string? cursor, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(new MailboxChanges([], cursor));

    public Task<SentEmail> SendAsync(OutgoingEmail email, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Email is not configured (Email:Mode is None).");
}
