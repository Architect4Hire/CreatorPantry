namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Which tiers of the reference catalogue a host seeds.
/// </summary>
/// <param name="IncludeDevelopmentSampleData">
/// Whether to seed Tier B — the sample ingredient set with its illustrative densities and traits. The Tier A
/// catalogue (units, vocabularies) is always seeded: it is definitional rather than licensed, and a database
/// without measurement units cannot resolve a recipe line at all.
/// </param>
internal sealed record ReferenceSeedOptions(bool IncludeDevelopmentSampleData);
