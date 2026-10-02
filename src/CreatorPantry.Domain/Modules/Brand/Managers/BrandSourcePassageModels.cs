namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// One source document version a caller wants passages from, named the way a creator names it.
/// </summary>
/// <remarks>
/// A document and a version <em>number</em>, never a version id or a chunk-set id. The number is what a
/// creator sees and what a guide's citations already use, and resolving it server-side is what keeps a caller
/// from naming a row by an id it should not have been able to guess.
/// </remarks>
public sealed record BrandSourcePassageSelector(Guid DocumentId, int VersionNumber);

/// <summary>
/// One passage of one source document version: creator text, cut by the chunker and stored as a
/// <c>BrandSourceChunk</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PassageId"/> is the chunk's own row id, which is what makes a citation verifiable rather than
/// merely well formed: a citation naming it can be checked against the rows this request actually read, and
/// the row is workspace-owned so another workspace's passage cannot be named at all.
/// </para>
/// <para>
/// <see cref="Text"/> is the creator's own words, exactly as the artifact has them — untrusted content, to be
/// fenced as a reference segment and never treated as instruction.
/// </para>
/// </remarks>
public sealed record BrandSourcePassageServiceModel(
    Guid PassageId,
    Guid DocumentId,
    int VersionNumber,
    int Ordinal,
    string Text);

/// <summary>
/// The passages a request could actually be given, and which of the versions it named could supply none.
/// </summary>
/// <param name="Unavailable">
/// The selectors that yielded nothing, for any reason — never issued, another workspace's, removed, a version
/// number the document does not have, text not extracted yet, or not embedded yet. The reasons are
/// deliberately not distinguished, for the reason <c>BrandErrorCodes.SourceNotFound</c> gives: a caller who
/// could tell them apart could learn that a document exists somewhere it cannot see it.
/// </param>
/// <remarks>
/// Reporting the gap rather than failing is what lets the caller state thin evidence instead of hiding it: a
/// guide proposal drawn from two of four selected documents has to say so.
/// </remarks>
public sealed record BrandSourcePassageSetServiceModel(
    IReadOnlyList<BrandSourcePassageServiceModel> Passages,
    IReadOnlyList<BrandSourcePassageSelector> Unavailable);
