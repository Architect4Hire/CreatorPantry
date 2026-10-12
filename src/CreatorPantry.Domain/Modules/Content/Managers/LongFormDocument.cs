using System.Text.Json;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The whole state of a long-form document as one revision stores it: the section plan, and each section's
/// words once it has them.
/// </summary>
/// <remarks>
/// A plan and a finished article are the same shape. The order of <see cref="Sections"/> is the plan order, and
/// a section with a null <see cref="LongFormSection.Body"/> is planned and not yet written. Private creator
/// content: never logged.
/// </remarks>
public sealed record LongFormDocument(IReadOnlyList<LongFormSection> Sections)
{
    /// <summary>The <c>SchemaVersion</c> a revision holding this shape carries.</summary>
    public const int CurrentSchemaVersion = 1;

    // Enums are written as numbers, which is the default and is what "append, never renumber" protects.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static LongFormDocument Deserialize(string content) =>
        JsonSerializer.Deserialize<LongFormDocument>(content, Options)
        ?? throw new JsonException("A long-form revision's content was null.");
}

/// <summary>One section of a long-form document.</summary>
/// <param name="Id">Stable across revisions, so a section can be regenerated, edited or settled by name.</param>
/// <param name="Role">From the closed vocabulary; stored as its number.</param>
/// <param name="Heading">The creator-visible heading.</param>
/// <param name="Intent">One line on what the section is for.</param>
/// <param name="Body">Markdown, or null while the section is planned and not yet written.</param>
/// <param name="Settled">
/// A creator-set working flag so progress can be shown. Not acceptance: acceptance is of the document (B-33).
/// </param>
/// <param name="SourceReferenceId">
/// For a newsletter item, the creative-context reference it covers, as the revision's recipe pin recorded it.
/// </param>
/// <param name="Provenance">Where the body came from, or null while there is none.</param>
public sealed record LongFormSection(
    Guid Id,
    LongFormSectionRole Role,
    string Heading,
    string Intent,
    string? Body,
    bool Settled,
    Guid? SourceReferenceId,
    LongFormSectionProvenance? Provenance);

/// <summary>Where one section's body came from.</summary>
/// <param name="Source">Generated, or the creator's own edit.</param>
/// <param name="AiProposalId">The AI proposal that wrote it, when generated.</param>
/// <param name="ReusedEditorialRevisionId">
/// The accepted editorial revision this body reuses prose from, so it can be attributed (B-36).
/// </param>
/// <param name="WrittenInRevisionNumber">The revision in which this body first appeared as it reads now.</param>
public sealed record LongFormSectionProvenance(
    ContentRevisionSource Source,
    Guid? AiProposalId,
    string? PromptTemplateId,
    string? PromptTemplateVersion,
    string? PromptTemplateBodyChecksum,
    Guid? ReusedEditorialRevisionId,
    int WrittenInRevisionNumber);
