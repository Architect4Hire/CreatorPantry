namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>What a creator chose to work from when a picture is planned (AF.3.2).</summary>
/// <remarks>
/// Starts at one: zero is not a choice, so a context whose source was never set is refused by the range check
/// rather than quietly read as the description. Stored as its integer, so members are appended and never
/// renumbered.
/// </remarks>
public enum CreativeContextBriefSource
{
    /// <summary>The picture the creator described, in their own words.</summary>
    Description = 1,

    /// <summary>The idea they picked, in the wording it was suggested in.</summary>
    Idea = 2,

    /// <summary>Their description with the picked idea's wording below it.</summary>
    Combined = 3,
}
