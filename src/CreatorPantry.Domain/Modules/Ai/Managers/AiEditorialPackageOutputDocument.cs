namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The structured answer RCPUB-001's editorial package task is held to. The document is also the schema:
/// <see cref="AiEditorialPackageOutputSchema.Json"/> exports it for the prompt, so the shape a model is asked for
/// and the shape it is judged against are one definition.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Prose only.</strong> There is nowhere in this shape to put a quantity, a time, a temperature, a
/// yield or a step: those belong to the recipe, which this task reads and never changes. Numbers a model
/// writes into prose anyway are found by <see cref="AiEditorialClaimScanner"/>, in code, after validation.
/// </para>
/// <para>
/// Every section is optional because the request chooses which to write, and an empty package is a valid
/// answer. Nothing is repaired: a mis-shaped answer fails.
/// </para>
/// </remarks>
public sealed record AiEditorialPackageOutputDocument
{
    public required string SchemaVersion { get; init; }

    public AiEditorialSections Sections { get; init; } = new();

    /// <summary>The model's own cautions. The server's findings are added to these and cannot be removed by them.</summary>
    public IReadOnlyList<AiEditorialPackageOutputWarning> Warnings { get; init; } = [];
}

public sealed record AiEditorialSections
{
    public AiEditorialText? Headnote { get; init; }

    public AiEditorialText? Introduction { get; init; }

    public IReadOnlyList<AiEditorialText> Tips { get; init; } = [];

    public IReadOnlyList<AiEditorialSubstitution> Substitutions { get; init; } = [];

    public AiEditorialText? StorageReheating { get; init; }

    public IReadOnlyList<AiEditorialFaq> Faq { get; init; } = [];

    public AiEditorialText? Cta { get; init; }
}

public sealed record AiEditorialText
{
    public required string Text { get; init; }
}

/// <summary>
/// A culinary suggestion for one ingredient line. Deliberately has no allergen, diet or safety field:
/// distinguishing taste and texture from suitability is ai.md's rule, and a shape that cannot express a
/// suitability claim cannot make one.
/// </summary>
public sealed record AiEditorialSubstitution
{
    /// <summary>The id the pinned version gives the ingredient line. Checked against it afterwards.</summary>
    public required Guid LineId { get; init; }

    public required string Suggestion { get; init; }

    public required string CulinaryNote { get; init; }
}

public sealed record AiEditorialFaq
{
    public required string Question { get; init; }

    public required string Answer { get; init; }
}

public sealed record AiEditorialPackageOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>The section the caution is about, or null for the package as a whole.</summary>
    public AiEditorialSection? Section { get; init; }
}

/// <summary>The seven sections of an editorial package. Stored and requested by name; append, never renumber.</summary>
public enum AiEditorialSection
{
    Headnote = 1,
    Introduction = 2,
    Tips = 3,
    Substitutions = 4,
    StorageReheating = 5,
    Faq = 6,
    Cta = 7,
}

public static class AiEditorialSectionCatalog
{
    public static IReadOnlyList<AiEditorialSection> All { get; } = Enum.GetValues<AiEditorialSection>();

    /// <summary>The section a request names, matched without regard to case, or null.</summary>
    public static AiEditorialSection? Parse(string? name) =>
        Enum.GetNames<AiEditorialSection>().FirstOrDefault(known => string.Equals(known, name?.Trim(), StringComparison.OrdinalIgnoreCase)) is { } exact
            ? Enum.Parse<AiEditorialSection>(exact)
            : null;

    /// <summary>The sections the document actually carries something in.</summary>
    public static IReadOnlySet<AiEditorialSection> Present(AiEditorialSections sections)
    {
        var present = new HashSet<AiEditorialSection>();

        if (sections.Headnote is not null) present.Add(AiEditorialSection.Headnote);
        if (sections.Introduction is not null) present.Add(AiEditorialSection.Introduction);
        if (sections.Tips.Count > 0) present.Add(AiEditorialSection.Tips);
        if (sections.Substitutions.Count > 0) present.Add(AiEditorialSection.Substitutions);
        if (sections.StorageReheating is not null) present.Add(AiEditorialSection.StorageReheating);
        if (sections.Faq.Count > 0) present.Add(AiEditorialSection.Faq);
        if (sections.Cta is not null) present.Add(AiEditorialSection.Cta);

        return present;
    }
}

/// <summary>What one editorial request made available to the model; an answer naming anything outside it is refused.</summary>
/// <param name="Requested">The sections the request asked for.</param>
/// <param name="LineIds">The ingredient lines of the pinned version, the only ones a substitution may name.</param>
public sealed record AiEditorialRequestContext(IReadOnlySet<AiEditorialSection> Requested, IReadOnlySet<Guid> LineIds);
