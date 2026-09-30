namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <param name="LineId">The recipe ingredient line the suggestion is about.</param>
public sealed record AcceptedEditorialSubstitution(Guid LineId, string Suggestion, string CulinaryNote);

public sealed record AcceptedEditorialFaq(string Question, string Answer);

/// <summary>
/// An accepted editorial revision, read for an export. The creator's accepted words and nothing more: a part
/// the stored document lacks is absent, never filled in.
/// </summary>
/// <param name="RevisionNumber">The revision's own number, as its proposal's history lists it.</param>
/// <param name="IsCurrent">
/// Whether the revision was written against the recipe version asked about and has not since been marked for
/// review. A caller must not use the words of a revision that is not current.
/// </param>
public sealed record AcceptedEditorialServiceModel(
    int RevisionNumber,
    string? Headnote,
    string? Introduction,
    IReadOnlyList<string> Tips,
    IReadOnlyList<AcceptedEditorialSubstitution> Substitutions,
    string? StorageReheating,
    IReadOnlyList<AcceptedEditorialFaq> Faq,
    string? Cta,
    bool IsCurrent);
