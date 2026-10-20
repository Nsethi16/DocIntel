using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace DocIntel.Core.Authorization;

/// <summary>An immutable directory identity, distinct from an application tenant slug.</summary>
public sealed record ActorIdentity(
    [property: JsonPropertyName("directoryTenantId")] Guid DirectoryTenantId,
    [property: JsonPropertyName("objectId")] Guid ObjectId);

/// <summary>Application tenant permissions assigned by server-side membership configuration.</summary>
public enum TenantRole
{
    Reader,
    Processor,
    Admin,
    Reviewer
}

/// <summary>A tenant role plus explicit document access; an empty document set grants no document access.</summary>
public sealed record DocumentAccessScope(TenantRole? Role, IReadOnlySet<string>? AllowedJobIds)
{
    public static DocumentAccessScope Denied { get; } = new(null, Array.Empty<string>().ToFrozenSet());

    public static DocumentAccessScope Unrestricted { get; } = new(TenantRole.Admin, null);

    public bool CanRead => Role.HasValue;

    public bool CanAdmin => Role == TenantRole.Admin;

    public bool CanReview => Role is TenantRole.Reviewer or TenantRole.Processor or TenantRole.Admin;

    public bool CanProcess => Role is TenantRole.Processor or TenantRole.Admin && AllowedJobIds is null;

    public bool CanReadDocument(string jobId) =>
        CanRead && (AllowedJobIds is null || AllowedJobIds.Contains(jobId));
}

/// <summary>Reevaluates the stored run actor against current server-side membership and document permissions.</summary>
public interface ITenantAccessResolver
{
    Task<DocumentAccessScope> ResolveAsync(
        ActorIdentity? actor, string tenantId, CancellationToken cancellationToken = default);
}
