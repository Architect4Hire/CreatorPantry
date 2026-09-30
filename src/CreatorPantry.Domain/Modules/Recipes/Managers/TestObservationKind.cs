namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What an observation is about, so a test can be read back grouped rather than as one undifferentiated list.
/// </summary>
/// <remarks>
/// <para>
/// A classification of the note, not of the recipe. Nothing downstream may treat
/// <see cref="TestObservationKind.Timing"/> as a statement that the recipe's times are wrong — that is what an
/// issue is for.
/// </para>
/// <para>
/// Zero is <see cref="Unspecified"/> and is a legitimate value: a tester writing "the kitchen was very warm
/// today" is not obliged to file it under a heading, and forcing a choice would produce a worse answer than
/// no choice. <see cref="Other"/> is different — it says the tester looked at the list and none of it fitted.
/// </para>
/// </remarks>
public enum TestObservationKind
{
    /// <summary>The tester did not classify this note.</summary>
    Unspecified = 0,

    Texture = 1,

    Flavour = 2,

    Appearance = 3,

    Aroma = 4,

    /// <summary>How long something actually took, or when it was ready. Not a verdict on the recipe's times.</summary>
    Timing = 5,

    /// <summary>How hard a step was to carry out as written.</summary>
    Difficulty = 6,

    /// <summary>Classified by the tester as none of the above.</summary>
    Other = 7,
}
