using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Ingredients.Gateways;

/// <summary>How one read of reference evidence ended.</summary>
public enum ReferenceEvidenceOutcome
{
    Found = 1,

    /// <summary>The source has no such record. Not evidence of anything about the food.</summary>
    NotFound = 2,

    /// <summary>This host has no provider for an approved source. Terminal until it is configured.</summary>
    NotConfigured = 3,

    /// <summary>
    /// No approved source covers what was asked — an evidence kind nothing is approved for, or a record from a
    /// data set of the source that is not approved. Callers read this as <em>unknown</em>, never as absent.
    /// </summary>
    NoApprovedSource = 4,

    /// <summary>The source's quota is spent. Nothing is sent until the wait is over.</summary>
    Throttled = 5,

    /// <summary>The source refused the credential. Terminal until it is replaced.</summary>
    CredentialRejected = 6,

    /// <summary>Unreachable, timed out, or failed on its side, after the retries a read is allowed.</summary>
    Unavailable = 7,

    /// <summary>Answered with something this contract cannot read. Not worth another attempt.</summary>
    Unreadable = 8,
}

public enum EvidenceCoverageStatus
{
    /// <summary>An approved source covers the kind, and this host can reach it.</summary>
    Available = 1,

    /// <summary>An approved source covers the kind, but this host has no provider for it.</summary>
    NotConfigured = 2,

    /// <summary>Nothing is approved for the kind. Its evidence is unknown on every host.</summary>
    NoApprovedSource = 3,
}

/// <summary>Whether evidence of one kind can be had on this host, and from where.</summary>
/// <param name="SourceCode">The approved source's code, when there is one.</param>
public sealed record EvidenceCoverage(ReferenceEvidenceKind Kind, EvidenceCoverageStatus Status, string? SourceCode)
{
    /// <summary>The coverage of <paramref name="kind"/> on a host that can reach the sources named.</summary>
    public static EvidenceCoverage Of(ReferenceEvidenceKind kind, IReadOnlyCollection<string> reachableSources)
    {
        var source = EvidenceSourceCatalog.For(kind);

        return source is null ? new(kind, EvidenceCoverageStatus.NoApprovedSource, null)
            : reachableSources.Contains(source.Code) ? new(kind, EvidenceCoverageStatus.Available, source.Code)
            : new(kind, EvidenceCoverageStatus.NotConfigured, source.Code);
    }
}

/// <summary>
/// Where one piece of evidence came from, recorded in full on the evidence itself so that it can be cited, its
/// licence honoured, and its age stated wherever it ends up (AI-009/010, DATA-RQ-007).
/// </summary>
/// <param name="SourceVersion">
/// The data set and the date the source published this record, e.g. <c>sr-legacy/2019-04-01</c>. What a later
/// mapping compares to tell that its evidence has been superseded.
/// </param>
/// <param name="ExternalRecordId">The source's own id for the record. Evidence's alone — never on a recipe or ingredient.</param>
/// <param name="PublishedOn">When the source published this record, when it said.</param>
/// <param name="RetrievedAt">When CreatorPantry read it from the source — not when it was last read from a cache.</param>
public sealed record EvidenceProvenance(
    string SourceCode,
    string SourceName,
    ReferenceSourceKind SourceKind,
    string SourceVersion,
    string LicenseCode,
    string LicenseUrl,
    string Citation,
    string ExternalRecordId,
    DateOnly? PublishedOn,
    DateTimeOffset RetrievedAt);

/// <summary>One nutrient, as the source measured it in 100 g of the edible portion.</summary>
/// <param name="SourceLabel">The source's own name for the row it came from, e.g. "Energy (Atwater Specific Factors)".</param>
/// <param name="Derivation">How the source says the value was obtained ("Analytical", "Calculated"), when it said.</param>
public sealed record NutrientEvidence(
    NutrientCode Code,
    decimal AmountPer100Grams,
    NutrientUnit Unit,
    string SourceLabel,
    string? Derivation);

/// <summary>One household portion and what it weighs, as the source recorded it.</summary>
/// <param name="VolumeUnitCode">
/// The <c>MeasurementUnit.Code</c> of the volume this portion is measured in, when the source's unit is one;
/// null for a count, a weight or a unit the adapter does not recognise.
/// </param>
/// <param name="Condition">How the food was measured — "sifted", "packed" — when the source said.</param>
/// <param name="SourceLabel">The source's own words for the portion.</param>
public sealed record PortionEvidence(
    decimal Amount,
    decimal GramWeight,
    string? VolumeUnitCode,
    string? Condition,
    string SourceLabel)
{
    /// <summary>A stated mass for a stated volume: what a density reference is made of.</summary>
    public bool IsDensity => VolumeUnitCode is not null;
}

/// <summary>Everything an approved source says about one food that CreatorPantry is approved to read.</summary>
/// <remarks>
/// <strong>A nutrient that is not listed is unknown, never zero.</strong> The lists hold what the source reported
/// and this contract could read; a source's own zero arrives as a listed value with its derivation ("Assumed
/// zero"), and a missing row is missing evidence (recipes.md).
/// </remarks>
/// <param name="Description">The source's own name for the food. Never written over an ingredient's name.</param>
/// <param name="Nutrients">Present only when the source is approved for nutrition. Unlisted means unknown.</param>
/// <param name="Portions">Present only when the source is approved for density. Unlisted means unknown.</param>
public sealed record ReferenceFoodEvidence(
    EvidenceProvenance Provenance,
    string Description,
    IReadOnlyList<NutrientEvidence> Nutrients,
    IReadOnlyList<PortionEvidence> Portions);

/// <param name="Food">Present exactly when the outcome is <see cref="ReferenceEvidenceOutcome.Found"/>.</param>
/// <param name="RetryAfter">How long until the source will answer again, when it is throttled.</param>
public sealed record ReferenceFoodFetch(
    ReferenceEvidenceOutcome Outcome,
    ReferenceFoodEvidence? Food = null,
    TimeSpan? RetryAfter = null)
{
    public static ReferenceFoodFetch Of(ReferenceEvidenceOutcome outcome, TimeSpan? retryAfter = null) =>
        new(outcome, null, retryAfter);
}

/// <summary>One food a search found. A candidate, not evidence: its evidence is a fetch by its id.</summary>
public sealed record ReferenceFoodCandidate(
    string SourceCode,
    string ExternalRecordId,
    string Description,
    string SourceVersion,
    DateOnly? PublishedOn);

/// <param name="Candidates">Empty unless the outcome is <see cref="ReferenceEvidenceOutcome.Found"/>, and may be empty then.</param>
public sealed record ReferenceFoodSearchResult(
    ReferenceEvidenceOutcome Outcome,
    IReadOnlyList<ReferenceFoodCandidate> Candidates,
    TimeSpan? RetryAfter = null)
{
    public static ReferenceFoodSearchResult Of(ReferenceEvidenceOutcome outcome, TimeSpan? retryAfter = null) =>
        new(outcome, [], retryAfter);
}

/// <summary>
/// What a search for reference foods may ask: a platform ingredient's canonical name, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no way to build one from free text.</strong> A search leaves the platform — to a source's
/// servers and their request logs — and lands in a cache every workspace shares. A creator's own ingredient
/// line is workspace-owned intellectual property and may carry anything, so it never becomes a search term;
/// matching it to a platform ingredient first is the mapping's job (14.2), and the name searched for is then
/// the platform's.
/// </para>
/// </remarks>
public sealed class ReferenceFoodSearch
{
    private ReferenceFoodSearch(string term) => Term = term;

    public string Term { get; }

    public static ReferenceFoodSearch ForIngredient(Ingredient ingredient)
    {
        ArgumentNullException.ThrowIfNull(ingredient);

        var term = ingredient.CanonicalName.Trim();
        if (term.Length is 0 or > EvidencePolicy.SearchTermMaxLength)
        {
            throw new ArgumentException("An ingredient's canonical name is not a usable search term.", nameof(ingredient));
        }

        return new ReferenceFoodSearch(term);
    }
}

/// <summary>
/// The boundary to the approved sources of nutrition, density, allergen, dietary and food-safety evidence, in
/// the application's own terms (14.1).
/// </summary>
/// <remarks>
/// <para>
/// A gateway by backend.md's definition: it owns the provider call and nothing else. No provider DTO, status
/// code, header or message crosses it, and nothing it returns is an EF entity or an HTTP shape — evidence is
/// mapped into these records at the boundary, and becomes a reference fact only through a later, reviewed step.
/// </para>
/// <para>
/// <strong>It never throws for a provider failure.</strong> A caller cancelling is the one exception that
/// propagates. Every other way a read can end is one of <see cref="ReferenceEvidenceOutcome"/>.
/// </para>
/// <para>
/// <strong>An unapproved source is never read.</strong> A kind with no approved source answers
/// <see cref="EvidenceCoverageStatus.NoApprovedSource"/> without a call, on every host.
/// </para>
/// </remarks>
public interface IReferenceEvidenceGateway
{
    /// <summary>Whether evidence of <paramref name="kind"/> can be had on this host. Calls nothing.</summary>
    EvidenceCoverage Describe(ReferenceEvidenceKind kind);

    /// <summary>One food's evidence, by the source's id for it. Read-only at the source.</summary>
    Task<ReferenceFoodFetch> GetFoodAsync(string externalRecordId, CancellationToken cancellationToken);

    /// <summary>Foods in approved data sets that match a platform ingredient. Read-only at the source.</summary>
    Task<ReferenceFoodSearchResult> SearchFoodsAsync(ReferenceFoodSearch search, CancellationToken cancellationToken);
}

/// <summary>The gateway on a host with no evidence provider: it says so, and calls nothing.</summary>
/// <remarks>
/// Registered by default, so a clean clone starts and reads a stable "not configured" — and a kind nothing is
/// approved for still reads "no approved source", which is true whatever is configured.
/// </remarks>
public sealed class UnconfiguredReferenceEvidenceGateway : IReferenceEvidenceGateway
{
    public EvidenceCoverage Describe(ReferenceEvidenceKind kind) => EvidenceCoverage.Of(kind, []);

    public Task<ReferenceFoodFetch> GetFoodAsync(string externalRecordId, CancellationToken cancellationToken) =>
        Task.FromResult(ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.NotConfigured));

    public Task<ReferenceFoodSearchResult> SearchFoodsAsync(ReferenceFoodSearch search, CancellationToken cancellationToken) =>
        Task.FromResult(ReferenceFoodSearchResult.Of(ReferenceEvidenceOutcome.NotConfigured));
}
