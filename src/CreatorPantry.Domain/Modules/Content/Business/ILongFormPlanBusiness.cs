using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>The long-form shape rules, applied to a plan somebody proposes (LF.1.3, LF.2.1).</summary>
public interface ILongFormPlanBusiness
{
    LongFormPlanRulesServiceModel Describe(LongFormKind kind);

    /// <summary>
    /// What is wrong with a plan's shape; empty when nothing is. Ceilings and word targets do not enter into
    /// it — a plan has no bodies to measure.
    /// </summary>
    IReadOnlyList<LongFormPlanFindingServiceModel> Check(
        LongFormKind kind, IReadOnlyList<LongFormPlanSectionServiceModel> sections);
}

/// <inheritdoc cref="ILongFormPlanBusiness"/>
/// <remarks>
/// Pure and synchronous: it reads <see cref="LongFormPolicy"/> and runs <see cref="LongFormDocumentValidator"/>,
/// and touches no store. It exists so that the one definition of a valid plan is the one a generated outline
/// and a creator's edit are both held to.
/// </remarks>
internal sealed class LongFormPlanBusiness : ILongFormPlanBusiness
{
    public LongFormPlanRulesServiceModel Describe(LongFormKind kind) => new(
        LongFormPolicy.Version,
        kind,
        LongFormPolicy.RolesFor(kind),
        LongFormPolicy.TargetWords(kind),
        LongFormPolicy.HeadingMaxCharacters,
        LongFormPolicy.IntentMaxCharacters);

    public IReadOnlyList<LongFormPlanFindingServiceModel> Check(
        LongFormKind kind, IReadOnlyList<LongFormPlanSectionServiceModel> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        // Ids made here only so a finding can be traced back to a position; a plan under review has none yet.
        var ids = sections.Select(_ => Guid.NewGuid()).ToList();

        var document = new LongFormDocument(
        [
            .. sections.Select((section, index) => new LongFormSection(
                ids[index], section.Role, section.Heading, section.Intent, Body: null, Settled: false,
                SourceReferenceId: null, Provenance: null)),
        ]);

        return
        [
            .. LongFormDocumentValidator.Validate(kind, document).Findings
                .Where(finding => finding.Severity is LongFormFindingSeverity.Failure)
                .Select(finding => new LongFormPlanFindingServiceModel(
                    finding.Code,
                    finding.SectionId is { } id ? ids.IndexOf(id) : null)),
        ];
    }
}
