using System.Security.Cryptography;
using System.Text;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;

namespace DocIntel.Api.Channels;

public enum PersonalEmailPurpose { Mailbox, Sender }
public enum PersonalEmailConnectionStatus { Connected, ReconnectionRequired }

/// <summary>Private OAuth evidence. This type is never returned from a user-facing endpoint.</summary>
public sealed record PersonalEmailCredential
{
    public required Guid BindingId { get; init; }
    public required ActorIdentity Actor { get; init; }
    public required string HomeAccountId { get; init; }
    public required string Subject { get; init; }
    public required string GraphId { get; init; }
    public required string[] Addresses { get; init; }
    public required byte[] TokenCache { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
    public PersonalEmailConnectionStatus Status { get; init; }

    public string Profile(string configuredProfile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            configuredProfile + ":" + BindingId.ToString("N") + ":" + HomeAccountId)));
}

/// <summary>One-use connection intent, bound to an existing DocIntel actor before leaving the application.</summary>
public sealed record PendingPersonalEmail
{
    public required string RequestId { get; init; }
    public required ActorIdentity Actor { get; init; }
    public required PersonalEmailPurpose Purpose { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public string? ReturnRunId { get; init; }
}

/// <summary>Version-fenced private mailbox and sender state, encrypted by the storage implementations.</summary>
public sealed record PersonalEmailState
{
    public required string TenantId { get; init; }
    public long Version { get; init; }
    public PersonalEmailCredential? Mailbox { get; init; }
    public List<PersonalEmailCredential> Senders { get; init; } = [];
    public List<PendingPersonalEmail> Pending { get; init; } = [];

    public void Validate(string tenantId)
    {
        if (TenantId != tenantId || !ValidTenant(tenantId) || Version < 0 ||
            Senders is null || Pending is null || Senders.Count > 100 || Pending.Count > 64 ||
            Senders.Any(item => item is null) || Pending.Any(item => item is null) ||
            Senders.Select(item => item.Actor).Distinct().Count() != Senders.Count ||
            Senders.Select(item => item.HomeAccountId).Distinct(StringComparer.Ordinal).Count() != Senders.Count ||
            Pending.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count() != Pending.Count ||
            Pending.Any(item => item.Actor is null || item.Actor.DirectoryTenantId == Guid.Empty ||
                item.Actor.ObjectId == Guid.Empty || !Enum.IsDefined(item.Purpose) ||
                !ValidRequest(item.RequestId) || item.ReturnRunId is not null && !ValidRun(item.ReturnRunId)))
            throw new InvalidDataException("Personal email state is invalid.");
        foreach (var credential in Senders.Concat(Mailbox is null ? [] : [Mailbox]))
            if (credential is null || credential.BindingId == Guid.Empty || credential.Actor is null ||
                credential.Actor.DirectoryTenantId == Guid.Empty || credential.Actor.ObjectId == Guid.Empty ||
                !ValidIdentifier(credential.HomeAccountId) || !ValidIdentifier(credential.Subject) ||
                !ValidIdentifier(credential.GraphId) || credential.Addresses is not { Length: >= 1 and <= 2 } ||
                credential.Addresses.Any(address => !EmailChannelOptions.IsAddress(address)) ||
                credential.TokenCache is not { Length: > 0 and <= 524_288 } || !Enum.IsDefined(credential.Status))
                throw new InvalidDataException("Personal email credentials are invalid.");
    }

    public static bool ValidTenant(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => Char.IsAsciiLetterOrDigit(character) || character == '-');
    public static bool ValidRequest(string? value) => value is { Length: 64 } && value.All(Char.IsAsciiHexDigit);
    public static bool ValidRun(string? value) => value is { Length: 69 } &&
        value.StartsWith("mail-", StringComparison.Ordinal) && value.AsSpan(5).ToArray().All(Char.IsAsciiHexDigit);
    private static bool ValidIdentifier(string? value) =>
        value is { Length: > 0 and <= 512 } && !value.Any(Char.IsControl);
}

public sealed class PersonalEmailStateConflictException() :
    InvalidOperationException("The email connection changed. Refresh before retrying.");

public sealed class PersonalEmailConnectionRequiredException() :
    InvalidOperationException("Reconnect the Microsoft account before using this email connection.");

public interface IPersonalEmailStateStore
{
    Task<PersonalEmailState> GetAsync(string tenantId, CancellationToken cancellationToken);
    Task SaveAsync(PersonalEmailState state, long expectedVersion, CancellationToken cancellationToken);
}

public sealed record PersonalEmailToken(string HomeAccountId, string AccessToken,
    DateTimeOffset ExpiresAtUtc, byte[] TokenCache, string? IdToken = null);
public sealed record PersonalEmailProfile(string GraphId, string[] Addresses);

public interface IPersonalEmailOAuthClient
{
    Task<PersonalEmailToken> RedeemAsync(
        string code, string verifier, PersonalEmailPurpose purpose, CancellationToken cancellationToken);
    Task<PersonalEmailToken> AcquireAsync(
        PersonalEmailCredential credential, PersonalEmailPurpose purpose, CancellationToken cancellationToken);
    Task<PersonalEmailProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken);
}

public sealed record PersonalEmailStatus(bool Available, bool Connected, string State, string? Address);
