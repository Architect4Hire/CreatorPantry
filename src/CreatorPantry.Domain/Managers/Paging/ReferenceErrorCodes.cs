namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// Stable error codes for the reference read seam. Renaming one is a breaking API change.
/// </summary>
/// <remarks>
/// The suffix picks the HTTP status (see <c>ProblemResults.StatusFor</c>); these are all request errors, so
/// they all end <c>.invalid_request</c> and all map to 400. Reference data is global and public to any signed-in
/// caller, so there is nothing here whose existence needs hiding behind a 404.
/// </remarks>
public static class ReferenceErrorCodes
{
    /// <summary>A search term, filter, or page size that could not be accepted as written.</summary>
    public const string QueryInvalid = "reference.query.invalid_request";

    /// <summary>
    /// The cursor did not decode. Almost always a hand-edited or truncated value: a cursor is meant to be
    /// passed back exactly as it was received.
    /// </summary>
    public const string CursorInvalid = "reference.cursor.invalid_request";

    /// <summary>A batch-resolve request submitted too many candidates, or one candidate too long.</summary>
    public const string CandidatesInvalid = "reference.candidates.invalid_request";
}
