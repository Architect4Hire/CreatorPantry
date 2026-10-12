namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a writer of one section is given about the draft it belongs to (LF.3.1): the approved plan, how each
/// section already written begins, and which recipe versions the plan was made against.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It carries one body, and that only on a rewrite.</strong> Every other section is here as its
/// heading, its intent and its first line — enough for a section to follow what came before without repeating
/// it, and no more of the creator's prose than that purpose needs.
/// </para>
/// <para>
/// <strong>It says which workspace the draft row was read in.</strong> The read is made under the workspace
/// filter, and a draft that is another workspace's is not found rather than found and withheld. The id is
/// carried anyway, as the grounding package carries its own, so whoever sends this to a model can check it
/// against the workspace it is working for instead of assuming the filter held.
/// </para>
/// </remarks>
/// <param name="WorkspaceId">The workspace the draft row was read in. Never sent to a model.</param>
/// <param name="TargetIndex">The position of the section being written in <paramref name="Sections"/>.</param>
/// <param name="CurrentBody">The body the section already has, when it is being rewritten. Null on a first write.</param>
/// <param name="SectionMaxCharacters">The ceiling a body is measured against after it is written.</param>
public sealed record LongFormWritingContextServiceModel(
    Guid WorkspaceId,
    Guid DraftId,
    Guid CreativeContextId,
    LongFormKind Kind,
    int RevisionNumber,
    int TargetIndex,
    string? CurrentBody,
    IReadOnlyList<LongFormWritingSectionServiceModel> Sections,
    IReadOnlyList<LongFormWritingPinServiceModel> Pins,
    LongFormWordRange TargetWords,
    int SectionMaxCharacters);

/// <summary>One section of the plan, as a writer of another section sees it.</summary>
/// <param name="FirstLine">How the section begins, when it has been written; null when it has not.</param>
public sealed record LongFormWritingSectionServiceModel(
    Guid Id,
    LongFormSectionRole Role,
    string Heading,
    string Intent,
    Guid? SourceReferenceId,
    string? FirstLine);

/// <summary>One recipe version the revision was planned against, by the creative-context reference that named it.</summary>
public sealed record LongFormWritingPinServiceModel(Guid ReferenceId, Guid RecipeId, Guid RecipeVersionId);

/// <summary>How long one body is, counted the way the document is, and whether that is over the section ceiling.</summary>
public sealed record LongFormBodyMeasureServiceModel(int CharacterCount, int MaxCharacters, bool OverCeiling);
