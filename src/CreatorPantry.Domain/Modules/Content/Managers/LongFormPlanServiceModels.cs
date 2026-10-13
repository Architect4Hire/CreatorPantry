namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The long-form shape rules for one kind, as another module may be told them (LF.2.1).
/// </summary>
/// <remarks>
/// What crosses the module boundary in place of the policy itself: which roles the kind allows and how often,
/// the target length, and the two text caps. Figures only — nothing here is a rule a caller can change.
/// </remarks>
/// <param name="Version">Which rules these are; changes whenever a figure or a role rule does.</param>
public sealed record LongFormPlanRulesServiceModel(
    string Version,
    LongFormKind Kind,
    IReadOnlyList<LongFormRoleRule> Roles,
    LongFormWordRange TargetWords,
    int HeadingMaxCharacters,
    int IntentMaxCharacters);

/// <summary>One planned section, as a caller submits it to be checked: a role, a heading and an intent.</summary>
public sealed record LongFormPlanSectionServiceModel(LongFormSectionRole Role, string Heading, string Intent);

/// <summary>One thing wrong with a submitted plan's shape.</summary>
/// <param name="Code">One of <see cref="LongFormFindingCodes"/>.</param>
/// <param name="SectionIndex">The position of the section it is about, or null when it is about the plan.</param>
public sealed record LongFormPlanFindingServiceModel(string Code, int? SectionIndex);
