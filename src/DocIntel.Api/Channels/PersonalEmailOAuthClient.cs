using System.Net.Http.Headers;
using System.Text.Json;
using DocIntel.Core.Channels;
using Microsoft.Identity.Client;

namespace DocIntel.Api.Channels;

/// <summary>Microsoft-managed token acquisition for one explicitly selected personal account, never app-only mail.</summary>
public sealed class PersonalEmailOAuthClient(
    IHttpClientFactory clients, EmailChannelOptions options) : IPersonalEmailOAuthClient, IDisposable
{
    public const string HttpClientName = "DocIntelPersonalEmailOAuth";
    public const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";
    public const string Authority = "https://login.microsoftonline.com/consumers/v2.0";
    public const string Issuer = "https://login.microsoftonline.com/" + ConsumerTenantId + "/v2.0";
    public const string CallbackPath = "/auth/email/personal/callback";
    private readonly MsalHttpClientFactory authenticationHttp = new(clients.CreateClient(HttpClientName));

    public static string[] Scopes(PersonalEmailPurpose purpose) => purpose == PersonalEmailPurpose.Mailbox
        ? ["User.Read", "Mail.Read", "Mail.Send"] : ["User.Read"];

    public async Task<PersonalEmailToken> RedeemAsync(
        string code, string verifier, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        var application = Application();
        var result = await application.AcquireTokenByAuthorizationCode(Scopes(purpose), code)
            .WithPkceCodeVerifier(verifier).ExecuteAsync(cancellationToken);
        return Token(application, result, purpose);
    }

    public async Task<PersonalEmailToken> AcquireAsync(
        PersonalEmailCredential credential, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        var application = Application();
        application.UserTokenCache.DeserializeMsalV3(credential.TokenCache);
        var account = await application.GetAccountAsync(credential.HomeAccountId)
            ?? throw new PersonalEmailConnectionRequiredException();
        var result = await application.AcquireTokenSilent(Scopes(purpose), account).ExecuteAsync(cancellationToken);
        var token = Token(application, result, purpose);
        if (token.HomeAccountId != credential.HomeAccountId)
            throw new InvalidDataException("Microsoft returned a different personal account.");
        return token;
    }

    public async Task<PersonalEmailProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(accessToken) || accessToken.Any(Char.IsControl))
            throw new InvalidDataException("The personal account credential is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var client = clients.CreateClient(GraphMailboxClient.HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Microsoft did not acknowledge personal account verification.",
                null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(block, cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > 32_768)
                throw new InvalidDataException("Personal account verification exceeded its supported size.");
            await buffer.WriteAsync(block.AsMemory(0, count), cancellationToken);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 and <= 512 } graphId ||
            graphId.Any(Char.IsControl))
            throw new InvalidDataException("Microsoft returned no personal account identity.");
        var addresses = new[] { "mail", "userPrincipalName" }
            .Select(name => root.TryGetProperty(name, out var value) ? value.GetString() : null)
            .Where(EmailChannelOptions.IsAddress).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (addresses.Length == 0)
            throw new InvalidDataException("Microsoft returned no supported primary email address.");
        return new PersonalEmailProfile(graphId, addresses);
    }

    public void Dispose() => authenticationHttp.Dispose();

    private IConfidentialClientApplication Application()
    {
        options.Validate();
        return ConfidentialClientApplicationBuilder.Create(options.Personal.ClientId.ToString("D"))
            .WithClientSecret(options.Personal.ClientSecret)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.PersonalMicrosoftAccount)
            .WithRedirectUri(options.PublicOrigin.TrimEnd('/') + CallbackPath)
            .WithHttpClientFactory(authenticationHttp)
            .WithLegacyCacheCompatibility(false)
            .Build();
    }

    private static PersonalEmailToken Token(
        IConfidentialClientApplication application, AuthenticationResult result, PersonalEmailPurpose purpose)
    {
        var granted = result.Scopes.Select(scope =>
            scope.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase)
                ? scope["https://graph.microsoft.com/".Length..] : scope).ToArray();
        if (result.Account?.HomeAccountId.Identifier is not { Length: > 0 and <= 512 } homeAccount ||
            String.IsNullOrWhiteSpace(result.AccessToken) || result.ExpiresOn <= DateTimeOffset.UtcNow ||
            Scopes(purpose).Any(scope => !granted.Contains(scope, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Microsoft did not grant the required personal account permissions.");
        var cache = application.UserTokenCache.SerializeMsalV3();
        if (cache.Length is 0 or > 524_288)
            throw new InvalidDataException("The Microsoft token cache exceeded its supported size.");
        return new PersonalEmailToken(homeAccount, result.AccessToken, result.ExpiresOn, cache, result.IdToken);
    }

    private sealed class MsalHttpClientFactory(HttpClient client) : IMsalHttpClientFactory, IDisposable
    {
        public HttpClient GetHttpClient() => client;
        public void Dispose() => client.Dispose();
    }
}
