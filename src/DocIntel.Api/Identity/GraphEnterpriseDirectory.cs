using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;

namespace DocIntel.Api.Identity;

public sealed class EnterpriseDirectoryOptions
{
    public bool Enabled { get; set; }
    /// <summary>Isolated protocol tests only; requires a loopback Development host and loopback identity authority.</summary>
    public string? DevelopmentEndpoint { get; set; }
}

/// <summary>Read-only, bounded enterprise people lookup. Never accepts a browser-supplied Graph URL or token.</summary>
public sealed class GraphEnterpriseDirectory(
    IHttpClientFactory clients, TokenCredential credential, EnterpriseDirectoryOptions options,
    IdentityOptions identity) : IEnterpriseDirectory
{
    public const string HttpClientName = "DocIntelEnterpriseDirectory";
    private const string Fields =
        "id,displayName,mail,userPrincipalName,jobTitle,department,officeLocation,accountEnabled,userType";
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Expires, DirectoryPerson? Person)> _people = new();
    public bool IsConfigured => options.Enabled;
    public Guid DirectoryTenantId => identity.DirectoryTenantId;

    public async Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (query.Length is < 2 or > 80 || query.Any(Char.IsControl))
            throw new ArgumentException("Enter between 2 and 80 characters of a name or work email.");
        var literal = query.Replace("'", "''", StringComparison.Ordinal);
        return await QueryAsync("userType eq 'Member' and accountEnabled eq true and " +
            $"(startswith(displayName,'{literal}') or startswith(mail,'{literal}') or " +
            $"startswith(userPrincipalName,'{literal}'))", 20, cancellationToken);
    }

    public async Task<DirectoryPerson?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        if (!EmailChannelOptions.IsAddress(email)) return null;
        var literal = email.Replace("'", "''", StringComparison.Ordinal);
        var people = await QueryAsync(
            $"userType eq 'Member' and accountEnabled eq true and (mail eq '{literal}' or userPrincipalName eq '{literal}')",
            2, cancellationToken);
        var matching = people.Where(person => person.AccountEnabled && person.Member &&
            (String.Equals(person.Email, email, StringComparison.OrdinalIgnoreCase) ||
             String.Equals(person.UserPrincipalName, email, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matching.Count > 1) throw new InvalidDataException("The sender address has ambiguous directory ownership.");
        return matching.SingleOrDefault();
    }

    public async Task<DirectoryPerson?> GetAsync(Guid objectId, CancellationToken cancellationToken)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("A directory object ID is required.");
        if (_people.TryGetValue(objectId, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Person;
        using var response = await SendAsync($"users/{objectId:D}?$select={Fields}", cancellationToken);
        DirectoryPerson? person = null;
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            using var body = await ReadJsonAsync(response, cancellationToken);
            person = Parse(body.RootElement);
            if (person.ObjectId != objectId)
                throw new InvalidDataException("The directory returned a different user identity.");
        }
        if (_people.Count >= 1_024)
            foreach (var entry in _people.Where(entry => entry.Value.Expires <= DateTimeOffset.UtcNow))
                _people.TryRemove(entry.Key, out _);
        if (_people.Count < 1_024)
            _people[objectId] = (DateTimeOffset.UtcNow.AddSeconds(30), person);
        return person;
    }

    public async Task<DirectoryPhoto?> GetPhotoAsync(Guid objectId, CancellationToken cancellationToken)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("A directory object ID is required.");
        using var response = await SendAsync($"users/{objectId:D}/photos/96x96/$value", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not ("image/jpeg" or "image/png"))
            throw new InvalidDataException("The directory returned an unsupported profile photo format.");
        return new DirectoryPhoto(await ReadBytesAsync(response, 512 * 1024, cancellationToken), type);
    }

    private async Task<IReadOnlyList<DirectoryPerson>> QueryAsync(
        string filter, int maximum, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            $"users?$select={Fields}&$top={maximum}&$count=true&$filter={Uri.EscapeDataString(filter)}", cancellationToken);
        using var body = await ReadJsonAsync(response, cancellationToken);
        var people = body.RootElement.GetProperty("value").EnumerateArray().Select(Parse).ToList();
        if (people.Count > maximum) throw new InvalidDataException("The directory returned an oversized people page.");
        return people;
    }

    private async Task<HttpResponseMessage> SendAsync(string resource, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("The enterprise directory is not connected.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(options.DevelopmentEndpoint ?? "https://graph.microsoft.com/v1.0/"), resource));
        request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
        if (options.DevelopmentEndpoint is null)
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://graph.microsoft.com/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        using var client = clients.CreateClient(HttpClientName);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException("Enterprise directory lookup was not acknowledged.", null, status);
        }
        return response;
    }

    private static DirectoryPerson Parse(JsonElement item)
    {
        if (!Guid.TryParse(item.GetProperty("id").GetString(), out var id) || id == Guid.Empty)
            throw new InvalidDataException("The enterprise directory returned an invalid object ID.");
        string? Text(string property, int maximum = 256)
        {
            var value = item.TryGetProperty(property, out var field) ? field.GetString() : null;
            if (value?.Length > maximum || value?.Any(Char.IsControl) == true)
                throw new InvalidDataException("The enterprise directory returned an unsupported profile field.");
            return value;
        }
        var mail = Text("mail", 254);
        var upn = Text("userPrincipalName", 254) ?? String.Empty;
        var address = EmailChannelOptions.IsAddress(mail) ? mail! :
            EmailChannelOptions.IsAddress(upn) ? upn : String.Empty;
        return new DirectoryPerson(id, Text("displayName") ?? String.Empty, address, upn,
            Text("jobTitle"), Text("department"), Text("officeLocation"),
            item.GetProperty("accountEnabled").GetBoolean(),
            String.Equals(Text("userType"), "Member", StringComparison.Ordinal));
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await ReadBytesAsync(response, 256 * 1024, cancellationToken));

    private static async Task<byte[]> ReadBytesAsync(
        HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximum)
            throw new InvalidDataException("The enterprise directory response exceeded its supported size.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > maximum)
                throw new InvalidDataException("The enterprise directory response exceeded its supported size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return output.ToArray();
    }
}
