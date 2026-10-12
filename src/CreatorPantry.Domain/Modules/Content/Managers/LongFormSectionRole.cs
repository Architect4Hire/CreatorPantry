namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a section of a long-form document is for. A closed vocabulary, stored as its number inside a revision's
/// JSON; append, never renumber.
/// </summary>
/// <remarks>
/// Starts at 1 so a defaulted 0 is never a role. Which roles a <see cref="LongFormKind"/> allows, and how often,
/// is the long-form policy's to say (LF.1.3); this is only the numbering stored documents depend on.
/// </remarks>
public enum LongFormSectionRole
{
    // Blog article.
    Hook = 1,
    Context = 2,
    WhyItWorks = 3,
    IngredientNotes = 4,
    MethodNarrative = 5,
    Tips = 6,
    Variations = 7,
    Storage = 8,
    Faq = 9,
    Cta = 10,

    /// <summary>Shared by both kinds.</summary>
    Closing = 11,

    /// <summary>A creator-headed body section of a blog article.</summary>
    Body = 12,

    // Newsletter issue.
    Greeting = 20,
    Lead = 21,

    /// <summary>One source the issue covers; repeatable, one per source reference (B-35).</summary>
    Item = 22,
    Aside = 23,
    Signoff = 24,
}
