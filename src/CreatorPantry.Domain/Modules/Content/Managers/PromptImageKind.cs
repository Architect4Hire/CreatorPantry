namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a prompt's image is <em>for</em>. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// <para>
/// This is what the requirements call a prompt's "content type", and it is editorial rather than technical: the
/// purpose the image serves, beside the channel it is for. A <c>PromptRecord</c> holds no bytes, so a MIME type
/// here would describe a file another row owns — <c>GeneratedImage</c> (12.6) and <c>DamAsset</c> (12.9) carry the
/// media type of the actual object, authoritatively, and a copy on this row could only ever disagree with them.
/// </para>
/// <para>
/// A flat list on purpose. "Hero shot for Instagram" is a kind and a channel, not a nested taxonomy, and the two
/// axes are already separate columns.
/// </para>
/// </remarks>
public enum PromptImageKind
{
    /// <summary>The main shot a piece of content leads with.</summary>
    Hero = 0,

    /// <summary>One stage of the method, mid-cook.</summary>
    ProcessStep = 1,

    /// <summary>The ingredients laid out before cooking.</summary>
    IngredientLayout = 2,

    /// <summary>A plated, styled scene rather than the dish alone.</summary>
    StyledScene = 3,

    /// <summary>Sized and composed for a social feed.</summary>
    SocialTile = 4,

    /// <summary>A tall graphic for a pinning channel.</summary>
    PinGraphic = 5,

    /// <summary>A close detail — crumb, texture, a pour.</summary>
    DetailShot = 6,

    /// <summary>Something else the creator had in mind. The label says what.</summary>
    Other = 7,
}
