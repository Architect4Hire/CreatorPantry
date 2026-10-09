namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// What a creator has said one library asset shows, and the version it was read beside.
/// </summary>
/// <param name="AltText">
/// The asset's alt text, or null when nobody has written any. Authored, never inferred: nothing in this
/// codebase writes it from a file name, a recipe title or a prompt (media.md), so a caller may treat a value
/// here as a description of the picture and a null as "nobody has described it" — never as "nothing is in it".
/// </param>
/// <param name="VersionNumber">The version asked for, or the current one when none was.</param>
/// <param name="CurrentVersionNumber">
/// The asset's newest version. Alt text is one value on the asset, not one per version, so it is only known to
/// describe the pixels of this one: a caller holding an older <paramref name="VersionNumber"/> cannot tell
/// whether the words were written before or after the picture was replaced.
/// </param>
/// <remarks>
/// Deliberately this small. It crosses a module boundary to ground a generation, and a title, an object key or
/// a rights line would each be something a prompt has no business carrying.
/// </remarks>
public sealed record MediaAssetDescription(string? AltText, int VersionNumber, int CurrentVersionNumber);
