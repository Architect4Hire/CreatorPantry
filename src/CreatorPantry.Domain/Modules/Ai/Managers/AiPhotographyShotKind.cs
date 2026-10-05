namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What one planned frame of a photography concept is for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately the AI module's own enum, not the Content module's <c>PromptImageKind</c>, although
/// every member here shares a name with one of its.</strong> Two reasons, and the first is the contract:
/// <see cref="AiPhotographyConceptOutputDocument"/> <em>is</em> the schema the model is shown, so an enum
/// exported into it must contain exactly the values an answer may use. <c>PromptImageKind</c> also carries
/// <c>SocialTile</c>, <c>PinGraphic</c> and <c>Other</c> — an output format, an output format, and a shrug —
/// which are not shoot roles, so showing them and then refusing them would be a schema that disagrees with its
/// own validator.
/// </para>
/// <para>
/// The second is direction of dependency. A concept is planned before any prompt record exists, so the AI
/// module has no business naming a Content vocabulary to describe its own answer; <strong>12.4a owns the
/// mapping</strong>, when it writes an approved shot's prompt into the library and needs an
/// <c>ImageKind</c> for the row.
/// </para>
/// <para>
/// <strong>The names are not a coincidence and must not drift</strong>: every member here is spelled exactly as
/// its <c>PromptImageKind</c> counterpart, so that mapping is a lookup by name rather than a table someone has
/// to maintain. <c>AiPhotographyShotKindTests</c> fails if a member here has no counterpart there.
/// </para>
/// </remarks>
public enum AiPhotographyShotKind
{
    /// <summary>
    /// The main shot the content leads with. Exactly one per concept, and the one IMG-002 composes first.
    /// </summary>
    Hero = 0,

    /// <summary>One stage of the method, mid-cook.</summary>
    ProcessStep = 1,

    /// <summary>The ingredients laid out before cooking.</summary>
    IngredientLayout = 2,

    /// <summary>A plated, styled scene rather than the dish alone.</summary>
    StyledScene = 3,

    /// <summary>A close detail — crumb, texture, a pour.</summary>
    DetailShot = 4,
}
