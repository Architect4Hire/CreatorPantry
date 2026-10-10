using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One immutable body for one channel: the words, how they measured against the channel's limit, and the exact
/// sources they were written from.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: every update and delete is refused at
/// <c>SaveChanges</c>. A regeneration, a creator edit and a reaffirmation are each a new revision, and the
/// creator's decision is recorded on the <see cref="SocialPackageChannel"/>, never by editing this row — so an
/// accepted revision is exactly what was accepted for as long as it exists.
/// </para>
/// <para>
/// <strong>The recipe pin is the staleness contract, and it is optional.</strong> A piece of work need not be
/// about a recipe. When it is, <see cref="RecipeVersionId"/> names an immutable version, so "the recipe
/// changed" is a comparison of version numbers rather than a guess; when it is not, both recipe columns are
/// null and no recipe change can make the post stale.
/// </para>
/// <para>
/// <strong>Model provenance is the AI proposal's.</strong> <see cref="AiProposalId"/> names the proposal that
/// produced a generated body, and that immutable row already records provider, model and deployment. Repeating
/// them here would be a second copy with nothing to say which was right.
/// </para>
/// <para>
/// <see cref="Body"/> is post copy and private creator content: never logged, never in an audit summary, and
/// never a source of recipe facts.
/// </para>
/// </remarks>
public class SocialRevision : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid SocialPackageChannelId { get; set; }

    /// <summary>Sequential from 1 within one channel, unique there, and never reused.</summary>
    public int RevisionNumber { get; set; }

    /// <summary>The revision this was made from; null only for revision 1.</summary>
    public Guid? ParentRevisionId { get; set; }

    public ContentRevisionSource Source { get; set; }

    /// <summary>The AI proposal that produced it; set exactly when <see cref="Source"/> is AiGenerated.</summary>
    public Guid? AiProposalId { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>The body's length as the channel profile counts it; null exactly when not checked.</summary>
    public int? CharacterCount { get; set; }

    /// <summary>The limit the profile applied, or null when it set none or the body was not checked.</summary>
    public int? CharacterLimit { get; set; }

    public SocialLimitStatus LimitStatus { get; set; }

    /// <summary>The profile version that measured it, so a stored result can be explained after the profile moves.</summary>
    public string? ChannelProfileVersion { get; set; }

    /// <summary>The recipe the copy was written about, or null. Set together with <see cref="RecipeVersionId"/>.</summary>
    public Guid? RecipeId { get; set; }

    /// <summary>The immutable version of that recipe the copy was written against.</summary>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>The brand profile revision in force, or null when the workspace had no profile.</summary>
    public Guid? BrandProfileRevisionId { get; set; }

    /// <summary>The style guide version in force, or null when the post was written without one.</summary>
    public Guid? BrandStyleGuideVersionId { get; set; }

    public string? PromptTemplateId { get; set; }

    public string? PromptTemplateVersion { get; set; }

    public string? PromptTemplateBodyChecksum { get; set; }

    /// <summary>The creative context's concurrency token when its package was assembled, or null.</summary>
    public string? CreativeContextVersion { get; set; }

    /// <summary>The assembled context package's checksum (AF.1.5): which grounding words this was written on.</summary>
    public string? ContextPackageChecksum { get; set; }

    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
