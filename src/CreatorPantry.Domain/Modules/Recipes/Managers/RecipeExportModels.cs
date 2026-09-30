namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>The fixed layouts a recipe export (Markdown or PDF) may use. Templates change layout only, never content.</summary>
public enum RecipeExportTemplate
{
    /// <summary>Description, equipment, and every accepted editorial section.</summary>
    Standard = 0,

    /// <summary>Title, one line of time and yield, ingredients, instructions and the creator's notes.</summary>
    Compact = 1,
}

/// <summary>How ingredient quantities are presented. <see cref="AsWritten"/> never converts anything.</summary>
public enum RecipeUnitPresentation
{
    AsWritten = 0,
    Metric = 1,
    UsCustomary = 2,
}

/// <summary>Accepted editorial copy. Each part is optional; <see cref="IsCurrent"/> false discards all of it.</summary>
public sealed record RecipeExportEditorial
{
    public bool IsCurrent { get; init; }
    public string? Headnote { get; init; }
    public string? Introduction { get; init; }
    public IReadOnlyList<string> Tips { get; init; } = [];
    public IReadOnlyList<RecipeExportSubstitution> Substitutions { get; init; } = [];
    public string? StorageReheating { get; init; }
    public IReadOnlyList<RecipeExportFaq> Faq { get; init; } = [];
    public string? Cta { get; init; }
}

/// <param name="LineId">The snapshot ingredient line the suggestion is about.</param>
public sealed record RecipeExportSubstitution(Guid LineId, string Suggestion, string CulinaryNote);

public sealed record RecipeExportFaq(string Question, string Answer);
