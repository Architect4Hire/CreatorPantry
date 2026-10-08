using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// EF Core access for the pictures attached to a recorded test. Persistence only.
/// </summary>
/// <remarks>
/// Every read is scoped by the global query filter, so another workspace's test, issue or attachment is
/// <em>not found</em> rather than found and refused — which is what makes "unknown id" and "someone else's id"
/// one answer at the HTTP boundary (tenancy.md).
/// </remarks>
public interface ITestAttachmentRepository
{
    /// <summary>
    /// The status of the recipe one test belongs to, or <c>null</c> when this workspace has no such test on
    /// that recipe.
    /// </summary>
    Task<RecipeStatus?> FindRecipeStatusForRunAsync(Guid recipeId, Guid testRunId, CancellationToken cancellationToken);

    /// <summary>Whether that issue is one of that test's own.</summary>
    Task<bool> IssueBelongsToRunAsync(Guid testRunId, Guid testIssueId, CancellationToken cancellationToken);

    /// <summary>The test's attachments in order, read without tracking.</summary>
    Task<IReadOnlyList<TestAttachmentLink>> ListAsync(Guid testRunId, CancellationToken cancellationToken);

    /// <summary>One attachment of that test, tracked so it can be removed, or <c>null</c> when it has none with that id.</summary>
    Task<TestAttachmentLink?> FindAsync(Guid testRunId, Guid attachmentId, CancellationToken cancellationToken);

    /// <summary>The next position for an attachment of that test, so a new one lands at the end.</summary>
    Task<int> NextOrderAsync(Guid testRunId, CancellationToken cancellationToken);

    void Add(TestAttachmentLink attachment);

    void Remove(TestAttachmentLink attachment);
}

/// <inheritdoc cref="ITestAttachmentRepository"/>
internal sealed class TestAttachmentRepository(CreatorPantryDbContext context) : ITestAttachmentRepository
{
    public async Task<RecipeStatus?> FindRecipeStatusForRunAsync(
        Guid recipeId, Guid testRunId, CancellationToken cancellationToken) =>

        // One statement, and both ids in it: a test id that belongs to a different recipe is not found rather
        // than answered for. No WorkspaceId predicate — both sets are workspace-owned and filtered.
        await context.RecipeTestRuns
            .AsNoTracking()
            .Where(run => run.Id == testRunId && run.RecipeId == recipeId)
            .Join(
                context.Recipes.AsNoTracking(),
                run => run.RecipeId,
                recipe => recipe.Id,
                (run, recipe) => (RecipeStatus?)recipe.Status)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> IssueBelongsToRunAsync(Guid testRunId, Guid testIssueId, CancellationToken cancellationToken) =>
        context.TestIssues
            .AsNoTracking()
            .AnyAsync(issue => issue.Id == testIssueId && issue.RecipeTestRunId == testRunId, cancellationToken);

    public async Task<IReadOnlyList<TestAttachmentLink>> ListAsync(Guid testRunId, CancellationToken cancellationToken) =>
        await context.TestAttachmentLinks
            .AsNoTracking()
            .Where(attachment => attachment.RecipeTestRunId == testRunId)
            .OrderBy(attachment => attachment.SortOrder)
            .ToListAsync(cancellationToken);

    public Task<TestAttachmentLink?> FindAsync(Guid testRunId, Guid attachmentId, CancellationToken cancellationToken) =>
        context.TestAttachmentLinks.FirstOrDefaultAsync(
            attachment => attachment.Id == attachmentId && attachment.RecipeTestRunId == testRunId,
            cancellationToken);

    public async Task<int> NextOrderAsync(Guid testRunId, CancellationToken cancellationToken) =>
        await context.TestAttachmentLinks
            .AsNoTracking()
            .Where(attachment => attachment.RecipeTestRunId == testRunId)
            .Select(attachment => (int?)attachment.SortOrder)
            .MaxAsync(cancellationToken) + 1 ?? 0;

    public void Add(TestAttachmentLink attachment) => context.TestAttachmentLinks.Add(attachment);

    public void Remove(TestAttachmentLink attachment) => context.TestAttachmentLinks.Remove(attachment);
}

/// <summary>
/// Composes the persistence of a test's attachments and owns its transaction boundary (RCPUB-005).
/// </summary>
public interface ITestAttachmentDataLayer
{
    /// <inheritdoc cref="ITestAttachmentRepository.FindRecipeStatusForRunAsync"/>
    Task<TestAttachmentRunRecord?> FindRunAsync(Guid recipeId, Guid testRunId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ITestAttachmentRepository.IssueBelongsToRunAsync"/>
    Task<bool> IssueBelongsToRunAsync(Guid testRunId, Guid testIssueId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ITestAttachmentRepository.ListAsync"/>
    Task<IReadOnlyList<TestAttachmentLink>> ListAsync(Guid testRunId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ITestAttachmentRepository.FindAsync"/>
    Task<TestAttachmentLink?> FindAsync(Guid testRunId, Guid attachmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a new attachment at the end of the test's own, in one save.
    /// </summary>
    /// <param name="attachment">The attachment, complete except for its position, which this assigns.</param>
    /// <remarks>
    /// The position is read and then written, so two attachments saved to one test at the same moment can
    /// choose the same one. The unique index on <c>(WorkspaceId, RecipeTestRunId, SortOrder)</c> refuses the
    /// second, and that is reported as <see cref="TestAttachmentWriteOutcome.PositionTaken"/> rather than
    /// thrown: nothing was written, and the same request succeeds if sent again.
    /// </remarks>
    Task<TestAttachmentWriteOutcome> AttachAsync(TestAttachmentLink attachment, CancellationToken cancellationToken);

    /// <summary>Removes one attachment, and only that row. The asset it named is another module's and is not touched.</summary>
    Task DetachAsync(TestAttachmentLink attachment, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITestAttachmentDataLayer"/>
internal sealed class TestAttachmentDataLayer(
    CreatorPantryDbContext context,
    ITestAttachmentRepository attachments) : ITestAttachmentDataLayer
{
    public async Task<TestAttachmentRunRecord?> FindRunAsync(
        Guid recipeId, Guid testRunId, CancellationToken cancellationToken) =>
        await attachments.FindRecipeStatusForRunAsync(recipeId, testRunId, cancellationToken) is { } status
            ? new TestAttachmentRunRecord(status)
            : null;

    public Task<bool> IssueBelongsToRunAsync(Guid testRunId, Guid testIssueId, CancellationToken cancellationToken) =>
        attachments.IssueBelongsToRunAsync(testRunId, testIssueId, cancellationToken);

    public Task<IReadOnlyList<TestAttachmentLink>> ListAsync(Guid testRunId, CancellationToken cancellationToken) =>
        attachments.ListAsync(testRunId, cancellationToken);

    public Task<TestAttachmentLink?> FindAsync(Guid testRunId, Guid attachmentId, CancellationToken cancellationToken) =>
        attachments.FindAsync(testRunId, attachmentId, cancellationToken);

    public async Task<TestAttachmentWriteOutcome> AttachAsync(
        TestAttachmentLink attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        attachment.SortOrder = await attachments.NextOrderAsync(attachment.RecipeTestRunId, cancellationToken);
        attachments.Add(attachment);

        try
        {
            // One row, one save. The test's own row is not touched, so its concurrency token stays valid:
            // attaching a picture is not an edit of what the test recorded.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Nothing was written, and nothing may be left staged either, or the next save on this scope
            // would commit an attachment the caller was told had been refused.
            context.ChangeTracker.Clear();

            // Ask the question that matters rather than read engine error numbers: is the position this chose
            // now someone else's? If so this lost a race for it. If not, something else is wrong — an asset
            // removed in the same instant, say — and must not be reported as a collaborator's attachment.
            var next = await attachments.NextOrderAsync(attachment.RecipeTestRunId, cancellationToken);
            if (next > attachment.SortOrder)
            {
                return TestAttachmentWriteOutcome.PositionTaken;
            }

            throw;
        }

        return TestAttachmentWriteOutcome.Committed;
    }

    public async Task DetachAsync(TestAttachmentLink attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        attachments.Remove(attachment);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Already removed by someone else between the read and this save. The caller asked for it to be
            // gone and it is, so this is not a failure — but the tracker must not be left holding a delete
            // of a row that is not there.
            context.ChangeTracker.Clear();
        }
    }
}
