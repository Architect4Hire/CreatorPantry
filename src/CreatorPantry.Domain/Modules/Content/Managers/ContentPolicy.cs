namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Limits shared by the content module's configuration and, later, its validators.</summary>
public static class ContentPolicy
{
    public const int ReasonMaxLength = 500;

    public const int TemplateIdMaxLength = 200;

    public const int TemplateVersionMaxLength = 32;

    /// <summary><c>sha256:</c> plus 64 hex characters.</summary>
    public const int ChecksumMaxLength = 71;

    public const int MachineVersionMaxLength = 32;

    /// <summary>
    /// A <c>ContentChannel.Key</c> as a workspace-owned record stores it. Matches the catalogue's own keys; this
    /// module keeps its own constant rather than reaching for another module's policy type, which would not cross
    /// the boundary.
    /// </summary>
    public const int ChannelKeyMaxLength = 64;

    /// <summary>
    /// The longest post body the write seam takes, in UTF-16 units.
    /// </summary>
    /// <remarks>
    /// A ceiling against a runaway body, not a channel limit: what a channel allows is its writing profile's to
    /// say (content.md), and a body over that is stored and flagged rather than refused. The column itself is
    /// unbounded so no profile is ever constrained by this number.
    /// </remarks>
    public const int SocialBodyMaxLength = 20000;

    /// <summary>A channel writing profile's version string.</summary>
    public const int ChannelProfileVersionMaxLength = 32;

    /// <summary>A creative context's concurrency token: base64 of an eight-byte row version, with room to spare.</summary>
    public const int ContextVersionMaxLength = 64;

    /// <summary>
    /// A saved image prompt.
    /// </summary>
    /// <remarks>
    /// Bounded rather than <c>nvarchar(max)</c>: an image prompt is a paragraph or two, well under this, and a
    /// bounded column can be validated at the edge and searched by PRM-002 without the cost a MAX column brings.
    /// Raise it if a real prompt ever approaches it; do not quietly switch the column to MAX, which would make the
    /// search PRM-002 needs considerably worse.
    /// </remarks>
    public const int PromptTextMaxLength = 4000;

    /// <summary>The creator's own short name for a prompt.</summary>
    public const int PromptLabelMaxLength = 200;

    /// <summary>
    /// How much of a prompt a list page carries.
    /// </summary>
    /// <remarks>
    /// Enough to recognise a prompt by its opening, and far short of the <see cref="PromptTextMaxLength"/> a
    /// row may hold: a hundred-row page of full prompts would be the size of the library, which is what the
    /// detail route exists to avoid. Truncation happens in SQL, so the bytes are never fetched to be dropped.
    /// Raising it is a compatible change; lowering it is not, because a client may be rendering what it was
    /// given.
    /// </remarks>
    public const int PromptPreviewMaxLength = 200;
}
