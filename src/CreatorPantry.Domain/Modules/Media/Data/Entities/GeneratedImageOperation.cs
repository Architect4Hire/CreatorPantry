using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// One request to generate images from one prompt (IMG-003), and the lineage of what was asked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It carries the prompt as text and names no <c>PromptRecord</c>, which is a decision rather than an
/// omission.</strong> A prompt record is <see cref="IImmutableRecord"/>, so its
/// <c>GeneratedImageId</c> can only ever be set at insert — which means the prompt row is written
/// <em>after</em> an image is committed, not before. An operation holding a foreign key to it would close a
/// cycle (prompt → image → operation → prompt) that could never be fully populated in either direction. The
/// text here is what was actually sent; <see cref="AiProposalId"/> is where it came from.
/// </para>
/// <para>
/// <strong>No bytes and no URL.</strong> This table and its images hold metadata; the files live in private
/// staging storage under the opaque key on <see cref="GeneratedImage.ObjectKey"/>, and nothing here is or
/// becomes a public address (media.md).
/// </para>
/// <para>
/// <strong>Mutable, unlike a prompt record.</strong> A status moves, a lease is taken and renewed, and a
/// completion time is stamped — so this carries no <see cref="IImmutableRecord"/> marker and
/// <c>ImmutableRecordInterceptor</c> leaves it alone.
/// </para>
/// </remarks>
public sealed class GeneratedImageOperation : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    public GeneratedImageOperationStatus Status { get; set; }

    /// <summary>
    /// The prompt as it was sent to the provider, after any creator edit.
    /// </summary>
    /// <remarks>
    /// Authoritative, the same way <c>PromptRecord.Text</c> is: a creator's edit of a composed prompt is what
    /// gets generated from, and a row recording the model's draft instead would misreport what produced the
    /// image.
    /// </remarks>
    public string PromptText { get; set; } = string.Empty;

    /// <summary>What the provider should avoid, when the request carried any. Null when it did not.</summary>
    public string? AvoidText { get; set; }

    /// <summary>
    /// The IMG-002 proposal the prompt was composed from, when it was composed rather than written.
    /// </summary>
    /// <remarks>
    /// Workspace-paired to <c>AiProposal</c>, so another workspace's proposal is unrepresentable rather than
    /// merely refused. Null for a prompt the creator wrote themselves.
    /// </remarks>
    public Guid? AiProposalId { get; set; }

    /// <summary>How many images were asked for. Between one and four (<see cref="MediaPolicy"/>).</summary>
    public int VariantCount { get; set; }

    /// <summary>The provider the operation was run against, once a worker has chosen one.</summary>
    public string? ProviderName { get; set; }

    /// <summary>The model it asked for, once known.</summary>
    public string? ModelName { get; set; }

    /// <summary>The deployment that served it, when the provider distinguishes one.</summary>
    public string? ModelDeployment { get; set; }

    /// <summary>
    /// What went wrong, as a stable category. Null while nothing has.
    /// </summary>
    public string? FailureCategory { get; set; }

    /// <summary>A sanitized summary of the failure. Never the provider's raw body, which can quote the prompt.</summary>
    public string? FailureSummary { get; set; }

    /// <summary>
    /// The key that makes a retry return the first answer rather than buy a second generation.
    /// </summary>
    /// <remarks>
    /// Required and unique within the workspace. Image generation is the most expensive thing this product
    /// does, so a lost response must not become a second charge.
    /// </remarks>
    public string IdempotencyKey { get; set; } = string.Empty;

    public Guid RequestedByMembershipId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset StatusChangedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>How many times a worker has claimed this. Drives the requeue backoff.</summary>
    public int Attempts { get; set; }

    /// <summary>When a worker may claim it. Moved forward by the backoff after a failed attempt.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>The worker holding it, when one does.</summary>
    public Guid? LeasedBy { get; set; }

    /// <summary>When that lease lapses, so an abandoned claim can be recovered.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
