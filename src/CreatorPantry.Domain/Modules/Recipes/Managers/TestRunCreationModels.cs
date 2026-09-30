namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What the recipe looked like to a test-run write: whether it is visible at all, its editorial state, and
/// the version the request named — if it has one.
/// </summary>
/// <remarks>
/// Three answers in one record because they come from one lookup and are decided in order. A null
/// <see cref="VersionId"/> with a real <see cref="Status"/> means the recipe is there and that version number
/// is not, which is a different refusal from not finding the recipe at all.
/// </remarks>
public sealed record TestRunTarget(RecipeStatus Status, Guid? VersionId);

/// <summary>
/// What a create produced: the run, and the identities of the rows written beneath it.
/// </summary>
/// <remarks>
/// Identities rather than entities, exactly as <see cref="CreatedRecipe"/> is. The caller needs to name what
/// was written — a location header, a response, the ids a later resolution will address — and handing back
/// tracked entities would invite a second layer to read fields that were only ever loaded for the write.
/// </remarks>
public sealed record CreatedRecipeTestRun(
    Guid TestRunId,
    IReadOnlyList<Guid> ObservationIds,
    IReadOnlyList<Guid> IssueIds);

/// <summary>
/// What a caller is told about a test run that was just recorded.
/// </summary>
/// <remarks>
/// <para>
/// A ServiceModel, not <see cref="CreatedRecipeTestRun"/>: that record is what the DataLayer hands back, and
/// publishing it directly would tie the wire format to a persistence result.
/// </para>
/// <para>
/// <see cref="ObservationIds"/> and <see cref="IssueIds"/> are in submitted order, which is what makes them
/// usable: a client that sent three issues gets three ids and knows which is which. Without them the only way
/// to resolve an issue would be to list the run back, and a create that leaves the caller unable to act on
/// what it created is a round trip with no purpose.
/// </para>
/// <para>
/// <strong>The concurrency token is here for a concrete reason:</strong> without it a client that had just
/// recorded a test could not edit it, because the edit requires a token and — until the history seam lands —
/// there is no route that would hand one over. A create that leaves the caller unable to act on what it created
/// is a round trip with no purpose, which is the same argument the id lists below make.
/// </para>
/// <para>
/// No storage URL, no signed link and no asset id, because this route links no media (see
/// <see cref="CreateRecipeTestRunViewModel"/>).
/// </para>
/// </remarks>
/// <param name="ConcurrencyToken">
/// The token the first edit to this test must quote.
/// </param>
public sealed record CreatedRecipeTestRunServiceModel(
    Guid TestRunId,
    Guid RecipeId,
    Guid RecipeVersionId,
    int SourceVersionNumber,
    TestRunOutcome Outcome,
    DateTimeOffset TestedAt,
    DateTimeOffset CreatedAt,
    string ConcurrencyToken,
    IReadOnlyList<Guid> ObservationIds,
    IReadOnlyList<Guid> IssueIds);
