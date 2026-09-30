using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One immutable state of a content package: its words, and the exact sources it was written against.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: every update and delete is refused at
/// <c>SaveChanges</c>. The creator's decision about a revision is recorded on the proposal and in
/// <see cref="ContentProposalTransition"/>, never by editing this row, so an accepted revision is exactly what
/// was accepted for as long as it exists. A correction, a regeneration and a reaffirmation are each a new
/// revision. Nothing deletes one: the foreign keys into it are <c>Restrict</c>.
/// </para>
/// <para>
/// <strong>The pins are the staleness contract.</strong> <see cref="RecipeVersionId"/> is an immutable
/// version, so "the recipe changed" is a comparison of ids rather than a guess. The voice pin has no foreign
/// key until <c>BrandStyleGuideVersion</c> (Phase 11A) exists, like <c>RecipeVersion.AiProposalId</c> before
/// its target did; it gains one then. The template pins carry the body checksum, because a version string
/// alone is a claim.
/// </para>
/// <para>
/// <see cref="Content"/> is the reviewable artifact, as <c>AiStructuredChange</c> is, and is never logged.
/// It is versioned JSON (<see cref="SchemaVersion"/>) of named sections. It is prose about the recipe: it
/// carries no recipe facts that the recipe does not own.
/// </para>
/// </remarks>
public class ContentRevision : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ContentProposalId { get; set; }

    /// <summary>
    /// Repeated from the proposal so the recipe-version pin can be constrained to a version <em>of that
    /// recipe</em>. The composite key to the proposal is what keeps the two equal.
    /// </summary>
    public Guid RecipeId { get; set; }

    /// <summary>Sequential from 1 within one proposal, unique there, and never reused.</summary>
    public int RevisionNumber { get; set; }

    /// <summary>The revision this was made from; null only for revision 1.</summary>
    public Guid? ParentRevisionId { get; set; }

    public ContentRevisionSource Source { get; set; }

    /// <summary>The AI proposal that produced it; set exactly when <see cref="Source"/> is AiGenerated.</summary>
    public Guid? AiProposalId { get; set; }

    /// <summary>The immutable recipe version the copy was written against. Required.</summary>
    public Guid RecipeVersionId { get; set; }

    /// <summary>The brand profile revision in force, or null when the workspace had no profile.</summary>
    public Guid? BrandProfileRevisionId { get; set; }

    /// <summary>The style guide version in force, or null. No foreign key yet; see the remarks.</summary>
    public Guid? BrandStyleGuideVersionId { get; set; }

    public string? PromptTemplateId { get; set; }

    public string? PromptTemplateVersion { get; set; }

    public string? PromptTemplateBodyChecksum { get; set; }

    public int SchemaVersion { get; set; }

    public string Content { get; set; } = string.Empty;

    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The pins as a value, for comparing with the current sources.</summary>
    public ContentSourcePins Pins => new(
        RecipeVersionId,
        BrandProfileRevisionId,
        BrandStyleGuideVersionId,
        PromptTemplateId,
        PromptTemplateVersion,
        PromptTemplateBodyChecksum);
}
