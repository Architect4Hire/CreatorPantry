namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Limits shared by the creative context's configuration and, later, its validators.</summary>
public static class CreativeContextPolicy
{
    /// <summary>The creator's own working name for a piece of work.</summary>
    public const int WorkingTitleMaxLength = 200;

    /// <summary>
    /// "The picture you have in mind", in the creator's words.
    /// </summary>
    /// <remarks>
    /// The same ceiling as <see cref="ContentPolicy.PromptTextMaxLength"/>, because this is what a creator may
    /// hand to the prompt step as the brief: a description the context accepted and the prompt then refused for
    /// length would be a limit discovered one screen too late.
    /// </remarks>
    public const int PictureBriefMaxLength = ContentPolicy.PromptTextMaxLength;

    /// <summary>
    /// The brief a creator chose to work from.
    /// </summary>
    /// <remarks>
    /// The same ceiling as <see cref="PictureBriefMaxLength"/>, which is room for a description with a picked
    /// idea's wording below it — what "combine" makes. That is deliberately more than a photography-concept
    /// request accepts: a combined brief that is too long to send has to be storable, or the creator's choice
    /// would be lost at the moment they are asked to shorten it. The request's own limit is enforced where the
    /// request is made.
    /// </remarks>
    public const int WorkingBriefMaxLength = PictureBriefMaxLength;

    /// <summary>
    /// How many sources one piece of work may name.
    /// </summary>
    /// <remarks>
    /// A context is what a generation is grounded on (AF.1.5), and that package is bounded: an unbounded list
    /// here would be an unbounded prompt there. Twenty is far more than a recipe, a concept, a handful of
    /// pictures and a prompt.
    /// </remarks>
    public const int MaxReferences = 20;

    /// <summary>How many channels one piece of work may be for. Above the catalogue's size, and bounded.</summary>
    public const int MaxChannels = 16;
}
