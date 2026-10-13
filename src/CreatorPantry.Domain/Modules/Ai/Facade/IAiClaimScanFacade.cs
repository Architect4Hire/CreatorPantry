using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>What a piece of text is checked against: the lines that state facts, and the creator's own prose.</summary>
/// <param name="FactLines">
/// Every line that states a figure the text may repeat — ingredient lines, steps, a yield, a time written out.
/// </param>
/// <param name="Prose">
/// The creator's own words that may support a claim or name a dish: titles, for the most part. A dish called
/// "Gluten-Free Seeded Loaf" may be called that.
/// </param>
/// <param name="StorageNotes">The creator's own storage notes, which alone support storage advice. Null when none.</param>
public sealed record AiClaimSourceServiceModel(
    IReadOnlyList<string> FactLines, string? Prose, string? StorageNotes = null);

/// <summary>One thing a deterministic scanner found in a piece of text.</summary>
/// <param name="Code">The scanner's stable code: <c>editorial.unsupported_number</c>, <c>seo.unsupported_metric</c>.</param>
public sealed record AiClaimFindingServiceModel(AiWarningKind Kind, string Code, string Message);

/// <summary>
/// The claim scanners the AI capabilities run over their own output, offered to other modules (LF.3.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Code, not a model.</strong> Nothing here calls a provider, reads a table or knows a workspace. It
/// is the editorial scanner and the SEO scanner's metric check, over whatever text and source it is handed —
/// so the module that owns a creator's long-form text can hold it to the same frames without owning a second
/// copy of the patterns.
/// </para>
/// <para>
/// <strong>It finds; it decides nothing.</strong> What to do about a finding — show it, store it, ignore one
/// that names a section's own subject — is the caller's.
/// </para>
/// </remarks>
public interface IAiClaimScanFacade
{
    /// <summary>
    /// Scans text for figures its source does not state, for safety, allergen, dietary, medical and nutrition
    /// claims, for storage advice and invented provenance its source does not support, and for search metrics.
    /// </summary>
    /// <param name="source">
    /// What the text may draw on. Null means there is nothing to check a figure against and none is checked:
    /// pass an empty source to have every figure reported.
    /// </param>
    IReadOnlyList<AiClaimFindingServiceModel> Scan(string text, AiClaimSourceServiceModel? source);
}

internal sealed class AiClaimScanFacade : IAiClaimScanFacade
{
    public IReadOnlyList<AiClaimFindingServiceModel> Scan(string text, AiClaimSourceServiceModel? source)
    {
        ArgumentNullException.ThrowIfNull(text);

        var facts = source is null
            ? null
            : AiEditorialSourceFacts.Of(source.FactLines, source.Prose, source.StorageNotes);

        return
        [
            .. CreativeContextClaimSource.Claims(text, facts)
                .Select(finding => new AiClaimFindingServiceModel(finding.Kind, finding.Code, finding.Message)),
        ];
    }
}
