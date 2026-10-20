using System.Net;
using DocIntel.Core.Authorization;

namespace DocIntel.Api.Identity;

public enum IdentityMode
{
    Entra,
    LocalDevelopment
}

public sealed class TenantMembership
{
    public Guid DirectoryTenantId { get; set; }
    public Guid ObjectId { get; set; }
    public string ApplicationTenantId { get; set; } = String.Empty;
    public TenantRole Role { get; set; } = TenantRole.Reader;
    public bool AllDocuments { get; set; }
    public bool ServicePrincipal { get; set; }
    public List<string> DocumentIds { get; set; } = [];
}

/// <summary>Server-owned identity configuration; secrets belong in user secrets or a managed secret provider.</summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";
    public IdentityMode Mode { get; set; } = IdentityMode.Entra;
    public Guid DirectoryTenantId { get; set; }
    public Guid ClientId { get; set; }
    public Guid ApiClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool UseManagedIdentityAssertion { get; set; }
    public string? ManagedIdentityClientId { get; set; }
    public string? PublicOrigin { get; set; }
    public string? DataProtectionDirectory { get; set; }
    public string? DataProtectionBlobUri { get; set; }
    public string? DataProtectionKeyIdentifier { get; set; }
    public string ApiAccessPermission { get; set; } = "DocIntel.Access";
    public List<TenantMembership> Memberships { get; set; } = [];
    public List<ActorIdentity> PlatformAdministrators { get; set; } = [];
    public bool AllowLoopbackAuthority { get; set; }
    public string? LoopbackAuthority { get; set; }
    public int ReadRequestsPerMinute { get; set; } = 300;
    public int WriteRequestsPerMinute { get; set; } = 20;

    public bool HasValidMemberships() =>
        Memberships is not null && PlatformAdministrators is not null &&
        Memberships.All(m => m is not null && m.DirectoryTenantId != Guid.Empty && m.ObjectId != Guid.Empty &&
            !String.IsNullOrWhiteSpace(m.ApplicationTenantId) && m.ApplicationTenantId.Length <= 128 &&
            Enum.IsDefined(m.Role) && m.DocumentIds is not null && m.DocumentIds.Count <= 1_000 &&
            m.DocumentIds.All(id => !String.IsNullOrWhiteSpace(id) && id.Length <= 512)) &&
        Memberships.GroupBy(m => (m.DirectoryTenantId, m.ObjectId, m.ApplicationTenantId))
            .All(g => g.Count() == 1) &&
        Memberships.GroupBy(m => (m.DirectoryTenantId, m.ObjectId))
            .All(g => g.Select(m => m.ServicePrincipal).Distinct().Count() == 1) &&
        PlatformAdministrators.All(a => a is not null &&
            a.DirectoryTenantId != Guid.Empty && a.ObjectId != Guid.Empty);

    public void Validate(IHostEnvironment environment, string? urls)
    {
        if (!Enum.IsDefined(Mode) || !HasValidMemberships() ||
            ReadRequestsPerMinute is < 1 or > 10_000 || WriteRequestsPerMinute is < 1 or > 1_000)
        {
            throw new InvalidOperationException("Identity mode or tenant membership configuration is invalid.");
        }
        if (Mode == IdentityMode.LocalDevelopment)
        {
            if (!environment.IsDevelopment() || String.IsNullOrWhiteSpace(urls) ||
                !urls.Split(';', StringSplitOptions.RemoveEmptyEntries).All(IsLoopbackUrl))
            {
                throw new InvalidOperationException(
                    "LocalDevelopment identity requires Development and explicit loopback-only listening URLs.");
            }
            return;
        }
        if (DirectoryTenantId == Guid.Empty || ClientId == Guid.Empty || ApiClientId == Guid.Empty ||
            ClientId == ApiClientId || String.IsNullOrWhiteSpace(ApiAccessPermission))
        {
            throw new InvalidOperationException(
                "Configure the Entra directory tenant, separate web/API application IDs, and API access permission.");
        }
        if (String.IsNullOrWhiteSpace(ClientSecret) && !UseManagedIdentityAssertion)
        {
            throw new InvalidOperationException(
                "Configure an Entra web application credential using a secret provider or managed identity federation.");
        }
        if (!String.IsNullOrWhiteSpace(DataProtectionDirectory) && !String.IsNullOrWhiteSpace(DataProtectionBlobUri))
            throw new InvalidOperationException("Select one identity key-ring storage location.");
        if (!String.IsNullOrWhiteSpace(DataProtectionBlobUri) &&
            (!Uri.TryCreate(DataProtectionBlobUri, UriKind.Absolute, out var keyBlob) ||
             !String.IsNullOrEmpty(keyBlob.UserInfo) || !String.IsNullOrEmpty(keyBlob.Query) ||
             !String.IsNullOrEmpty(keyBlob.Fragment) ||
             (keyBlob.Scheme != Uri.UriSchemeHttps &&
              !(environment.IsDevelopment() && IsLoopbackUrl(DataProtectionBlobUri)))))
            throw new InvalidOperationException("Identity key blobs require HTTPS, except a loopback Development emulator.");
        if (!String.IsNullOrWhiteSpace(DataProtectionKeyIdentifier) && !IsVersionlessKey(DataProtectionKeyIdentifier))
            throw new InvalidOperationException("Identity key encryption requires a versionless HTTPS Key Vault key identifier.");
        if (!environment.IsDevelopment() &&
            (!IsHttpsOrigin(PublicOrigin) ||
             (String.IsNullOrWhiteSpace(DataProtectionDirectory) &&
              (String.IsNullOrWhiteSpace(DataProtectionBlobUri) || String.IsNullOrWhiteSpace(DataProtectionKeyIdentifier)))))
        {
            throw new InvalidOperationException(
                "Production identity requires HTTPS PublicOrigin and protected persistent directory or Blob/Key Vault keys.");
        }
        if (AllowLoopbackAuthority && (!environment.IsDevelopment() || !IsLoopbackUrl(LoopbackAuthority) ||
            String.IsNullOrWhiteSpace(urls) ||
            !urls.Split(';', StringSplitOptions.RemoveEmptyEntries).All(IsLoopbackUrl)))
        {
            throw new InvalidOperationException("Loopback OIDC authorities are restricted to loopback Development hosts.");
        }
    }

    public static bool IsLoopbackUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && String.IsNullOrEmpty(uri.UserInfo) &&
        (String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)));

    private static bool IsHttpsOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        String.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" &&
        String.IsNullOrEmpty(uri.Query) && String.IsNullOrEmpty(uri.Fragment);

    private static bool IsVersionlessKey(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Query) && String.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["keys", var name] &&
        !String.IsNullOrWhiteSpace(name);
}
