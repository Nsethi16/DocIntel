using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using Azure.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using Microsoft.Extensions.Logging;

namespace DocIntel.Api.Channels;

/// <summary>Fixed-host Microsoft Graph mailbox access; credentials never follow redirects or caller-supplied URLs.</summary>
public sealed class GraphMailboxClient(
    IHttpClientFactory clients, TokenCredential credential, EmailChannelOptions options,
    ILogger<GraphMailboxClient>? logger = null) : IMailboxClient
{
    public const string HttpClientName = "DocIntelMailboxGraph";
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private string MailboxRoot => $"https://graph.microsoft.com/v1.0/users/{options.MailboxObjectId:D}";

    public async Task<MailboxChanges> ReadChangesAsync(string? continuation, CancellationToken cancellationToken,
        DateTimeOffset? receivedAfterUtc = null)
    {
        var initial = MailboxRoot + "/mailFolders/inbox/messages/delta?$select=id,receivedDateTime";
        if (receivedAfterUtc.HasValue)
            initial += "&$filter=" + Uri.EscapeDataString("receivedDateTime ge " +
                receivedAfterUtc.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        var url = continuation ?? initial;
        ValidateMailboxLink(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\", odata.maxpagesize=25");
        using var result = await SendAsync(request, cancellationToken);
        using var body = await ReadAsync(result, cancellationToken);
        var messages = new List<MailboxQuestion>();
        foreach (var item in body.RootElement.GetProperty("value").EnumerateArray())
        {
            if (item.TryGetProperty("@removed", out _)) continue;
            if (receivedAfterUtc.HasValue && item.GetProperty("receivedDateTime").GetDateTimeOffset() < receivedAfterUtc.Value)
                continue;
            var messageId = item.GetProperty("id").GetString() ??
                throw new InvalidDataException("Graph returned no mailbox message identity.");
            JsonDocument message;
            try
            {
                using var detail = new HttpRequestMessage(HttpMethod.Get, MailboxRoot +
                    "/messages/" + Uri.EscapeDataString(messageId) +
                    "?$select=id,subject,body,from,receivedDateTime,internetMessageHeaders");
                detail.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
                using var detailResult = await SendAsync(detail, cancellationToken);
                message = await ReadAsync(detailResult, cancellationToken, 128 * 1024);
            }
            catch (InvalidDataException)
            {
                logger?.LogWarning("An oversized mailbox message was not accepted; synchronization will continue.");
                continue;
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger?.LogWarning("A mailbox message was removed before ingestion; synchronization will continue.");
                continue;
            }
            using var messageBody = message;
            var entry = message.RootElement;
            if (entry.GetProperty("id").GetString() != messageId ||
                !entry.TryGetProperty("from", out var from) ||
                !from.TryGetProperty("emailAddress", out var address) ||
                !entry.TryGetProperty("body", out var content) ||
                !String.Equals(content.GetProperty("contentType").GetString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning("An unsupported mailbox message was not accepted; plaintext is required.");
                continue;
            }
            var automatic = entry.TryGetProperty("internetMessageHeaders", out var headers) &&
                headers.EnumerateArray().Any(header =>
                    (String.Equals(header.GetProperty("name").GetString(), "Auto-Submitted", StringComparison.OrdinalIgnoreCase) &&
                     !String.Equals(header.GetProperty("value").GetString(), "no", StringComparison.OrdinalIgnoreCase)) ||
                    (String.Equals(header.GetProperty("name").GetString(), "X-DocIntel-Automated-Message", StringComparison.OrdinalIgnoreCase) &&
                     String.Equals(header.GetProperty("value").GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            messages.Add(new MailboxQuestion(
                messageId,
                address.GetProperty("address").GetString() ?? String.Empty,
                entry.GetProperty("subject").GetString() ?? String.Empty,
                content.GetProperty("content").GetString() ?? String.Empty,
                entry.GetProperty("receivedDateTime").GetDateTimeOffset(), automatic));
            if (messages.Count > 100) throw new InvalidDataException("Graph returned an oversized mailbox page.");
        }
        var next = body.RootElement.TryGetProperty("@odata.nextLink", out var nextValue) ? nextValue.GetString() : null;
        var delta = body.RootElement.TryGetProperty("@odata.deltaLink", out var deltaValue) ? deltaValue.GetString() : null;
        if (next is not null) ValidateMailboxLink(next);
        if (delta is not null) ValidateMailboxLink(delta);
        if (next is null && delta is null) throw new InvalidDataException("Graph returned no durable mailbox continuation.");
        return new MailboxChanges(messages, next, delta);
    }

    public async Task<IReadOnlyList<string>> GetActorAddressesAsync(
        ActorIdentity actor, CancellationToken cancellationToken)
    {
        if (actor.DirectoryTenantId != options.TenantId || actor.ObjectId == Guid.Empty)
            throw new UnauthorizedAccessException("The caller belongs to a different mailbox directory.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{actor.ObjectId:D}?$select=id,mail,userPrincipalName");
        using var response = await SendAsync(request, cancellationToken);
        using var body = await ReadAsync(response, cancellationToken);
        if (!Guid.TryParse(body.RootElement.GetProperty("id").GetString(), out var id) || id != actor.ObjectId)
            throw new InvalidDataException("Graph returned a different directory user.");
        return new[] { "mail", "userPrincipalName" }
            .Select(name => body.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null)
            .Where(value => EmailChannelOptions.IsAddress(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken)
    {
        if (!EmailChannelOptions.IsAddress(recipient) || subject.Length > 512 || subject.Any(Char.IsControl) || text.Length > 128_000)
            throw new ArgumentException("A bounded message to one validated email destination is required.");
        using var request = new HttpRequestMessage(HttpMethod.Post, MailboxRoot + "/sendMail")
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    subject,
                    body = new { contentType = "Text", content = text },
                    toRecipients = new[] { new { emailAddress = new { address = recipient } } },
                    internetMessageHeaders = new[]
                    {
                        new { name = "X-Auto-Response-Suppress", value = "All" },
                        new { name = "X-DocIntel-Automated-Message", value = "true" }
                    }
                },
                saveToSentItems = true
            })
        };
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            throw new HttpRequestException("Graph did not acknowledge the message submission.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        options.Validate();
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var client = clients.CreateClient(HttpClientName);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException("Microsoft Graph mailbox operation was not acknowledged.", null, status);
        }
        return response;
    }

    private void ValidateMailboxLink(string link)
    {
        if (link.Length > 16_384 || !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            !uri.AbsolutePath.StartsWith($"/v1.0/users/{options.MailboxObjectId:D}/", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.EndsWith("/messages/delta", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The mailbox continuation does not belong to the configured Graph resource.");
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken,
        int maximumBytes = MaximumResponseBytes)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Graph returned an oversized response.");
        using var output = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("Graph response exceeded its limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return JsonDocument.Parse(output.ToArray());
    }
}
