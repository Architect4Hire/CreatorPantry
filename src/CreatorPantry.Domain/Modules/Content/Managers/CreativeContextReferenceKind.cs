namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>What one <c>CreativeContextReference</c> points at.</summary>
/// <remarks>
/// Starts at one: zero is not a kind, so a reference whose kind was never set is refused by the range check
/// rather than quietly read as a recipe. Stored as its integer, so members are appended and never renumbered.
/// </remarks>
public enum CreativeContextReferenceKind
{
    /// <summary>A recipe, optionally pinned to one of its versions.</summary>
    Recipe = 1,

    /// <summary>One concept from a recipe-concept request: the request and the concept inside it.</summary>
    RecipeConcept = 2,

    /// <summary>A DAM asset, optionally pinned to one of its versions.</summary>
    DamAsset = 3,

    /// <summary>A staged generated image.</summary>
    GeneratedImage = 4,

    /// <summary>A prompt from the prompt library.</summary>
    PromptRecord = 5,

    /// <summary>A post package written for this context.</summary>
    SocialPackage = 6,
}
