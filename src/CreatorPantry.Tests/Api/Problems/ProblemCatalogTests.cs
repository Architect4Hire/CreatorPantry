extern alias ApiService;

using System.Reflection;
using ApiService::CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Api.Problems;

/// <summary>
/// The error-code catalogue against the problem mapping: every conflict has a reviewed recovery, and every status
/// family carries the retry advice its remedy implies (API-002/003).
/// </summary>
/// <remarks>
/// The wire itself — what a client actually receives for each family — is <see cref="ProblemFamilyWireTests"/>.
/// </remarks>
public sealed class ProblemCatalogTests
{
    /// <summary>
    /// The 409s reviewed as conflicts with the record's <em>state</em>: re-reading shows why, but sending the same
    /// request again — fresh token or not — will not end differently. <c>retry: none</c>.
    /// </summary>
    /// <remarks>
    /// Names are not a guide here. <c>brand.guide.version.stale.conflict</c> and
    /// <c>content.social.source_stale.conflict</c> are about superseded <em>sources</em>, cured only by a new
    /// version, so they belong on this list and not in <see cref="ProblemResults.RereadCodes"/>.
    /// </remarks>
    private static readonly HashSet<string> ReviewedStateConflicts = new(StringComparer.Ordinal)
    {
        "ai.brandGuideProposal.conflict",
        "ai.proposal.conflict",
        "ai.recipeDraft.conflict",
        "ai.seoPackage.longFormDraft.not_accepted.conflict",
        "brand.guide.archived.conflict",
        "brand.guide.version.empty.conflict",
        "brand.guide.version.stale.conflict",
        "brand.guide.version.unapproved.conflict",
        "brand.guide.workingVersion.conflict",
        "brand.profile.exists.conflict",
        "brand.source.archived.conflict",
        "brand.source.extraction.not_retryable.conflict",
        "brand.source.extraction.pending.conflict",
        "brand.source.extraction.superseded.conflict",
        "content.creative_context.reference.duplicate.conflict",
        "content.long_form.draft.accepted.conflict",
        "content.long_form.draft.not_an_issue.conflict",
        "content.long_form.draft.not_writable.conflict",
        "content.long_form.export.not_accepted.conflict",
        "content.long_form.plan.locked.conflict",
        "content.long_form.plan.not_approved.conflict",
        "content.long_form.plan.not_started.conflict",
        "content.long_form.section.not_written.conflict",
        "content.long_form.section.settled.conflict",
        "content.long_form.section.superseded.conflict",
        "content.social.decision.conflict",
        "content.social.source_stale.conflict",
        "media.staged_image.conflict",
        "nutrition.mapping.state.conflict",
        "planning.work_task.completed.conflict",
        "planning.work_task.open.conflict",
        "publishing.already_published.conflict",
        "publishing.duplicate.conflict",
        "publishing.not_cancellable.conflict",
        "publishing.outcome_unknown.conflict",
        "publishing.platform.mismatch.conflict",
        "recipes.archived.conflict",
        "recipes.assetLink.duplicate.conflict",
        "recipes.assetLink.hero.conflict",
        "recipes.jsonLdExport.notApproved.conflict",
        "recipes.markdownExport.notApproved.conflict",
        "recipes.pdfExport.notApproved.conflict",
        "recipes.testAttachment.duplicate.conflict",
        "recipes.testIssue.removal.conflict",
        "recipes.testIssue.resolved.conflict",
        "recipes.transition.blocked.conflict",
        "tenancy.workspace.conflict",
        "workflows.run.at_first_step.conflict",
        "workflows.run.not_in_progress.conflict",
        "workflows.step.already_done.conflict",
        "workflows.step.not_current.conflict",
        BrandErrorCodes.SetupSessionCompleted,
        BrandErrorCodes.SetupSessionNotFinished,
    };

    /// <summary>
    /// Every string constant in the domain that answers 409. Read from the assembly rather than listed, so a new
    /// conflict code is found by this test the day it is declared.
    /// </summary>
    private static IReadOnlyList<string> DeclaredConflictCodes() =>
    [
        .. typeof(RecipeErrorCodes).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: true, IsSealed: true })
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => !value.Contains(' ') && ProblemResults.StatusFor(value) == StatusCodes.Status409Conflict)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    [Fact]
    public void The_catalogue_is_found()
    {
        // Guards the reflection above: a catalogue it silently stopped reading would make every check here pass.
        var declared = DeclaredConflictCodes();

        Assert.Contains(RecipeErrorCodes.RecipeConflict, declared);
        Assert.Contains(BrandErrorCodes.SetupSessionConflict, declared);
        Assert.True(declared.Count >= 85, $"Only {declared.Count} conflict codes were found.");
    }

    /// <summary>
    /// A conflict with no reviewed recovery would answer <c>retry: none</c> by default — which is wrong for a
    /// stale token, and silently so. Classify it in <see cref="ProblemResults"/> or on the reviewed list above.
    /// </summary>
    [Fact]
    public void Every_conflict_code_has_a_reviewed_recovery()
    {
        var unclassified = DeclaredConflictCodes()
            .Where(code => !ProblemResults.RereadCodes.Contains(code)
                && !ProblemResults.SameRequestCodes.Contains(code)
                && !ReviewedStateConflicts.Contains(code))
            .ToList();

        Assert.True(unclassified.Count == 0, "Unclassified conflict codes: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void No_code_is_classified_twice_or_names_a_code_that_no_longer_exists()
    {
        var declared = DeclaredConflictCodes().Append(ProblemResults.ConcurrencyConflictCode).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(ProblemResults.RereadCodes.Intersect(ProblemResults.SameRequestCodes));
        Assert.Empty(ProblemResults.RereadCodes.Intersect(ReviewedStateConflicts));
        Assert.Empty(ProblemResults.SameRequestCodes.Intersect(ReviewedStateConflicts));
        Assert.Empty(ReviewedStateConflicts.Except(declared));
        Assert.Empty(ProblemResults.RereadCodes.Except(declared));
        Assert.Empty(ProblemResults.SameRequestCodes.Except(declared));
    }

    [Theory]
    [InlineData(RecipeErrorCodes.RecipeConflict, ProblemRetry.Reread)]
    [InlineData(ProblemResults.ConcurrencyConflictCode, ProblemRetry.Reread)]
    [InlineData("media.asset.version_taken.conflict", ProblemRetry.Same)]
    [InlineData(RecipeErrorCodes.RecipeArchivedConflict, ProblemRetry.None)]
    [InlineData("brand.guide.version.stale.conflict", ProblemRetry.None)]
    public void A_conflicts_recovery_comes_from_its_code(string code, string expected) =>
        Assert.Equal(expected, ProblemResults.RetryFor(code, ProblemResults.StatusFor(code)));

    /// <summary>Wait for an allowance or a dependency; anything else — a 500 included — is not retried as it stands.</summary>
    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, ProblemRetry.None)]
    [InlineData(StatusCodes.Status401Unauthorized, ProblemRetry.None)]
    [InlineData(StatusCodes.Status403Forbidden, ProblemRetry.None)]
    [InlineData(StatusCodes.Status404NotFound, ProblemRetry.None)]
    [InlineData(StatusCodes.Status409Conflict, ProblemRetry.None)]
    [InlineData(StatusCodes.Status410Gone, ProblemRetry.None)]
    [InlineData(StatusCodes.Status413PayloadTooLarge, ProblemRetry.None)]
    [InlineData(StatusCodes.Status422UnprocessableEntity, ProblemRetry.None)]
    [InlineData(StatusCodes.Status429TooManyRequests, ProblemRetry.Later)]
    [InlineData(StatusCodes.Status500InternalServerError, ProblemRetry.None)]
    [InlineData(StatusCodes.Status502BadGateway, ProblemRetry.Later)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, ProblemRetry.Later)]
    [InlineData(StatusCodes.Status504GatewayTimeout, ProblemRetry.Later)]
    public void A_status_without_a_classified_code_implies_its_own_advice(int status, string expected)
    {
        Assert.Equal(expected, ProblemRetry.ForStatus(status));
        Assert.Equal(expected, ProblemResults.RetryFor(ProblemResults.CodeForStatus(status), status));
    }

    [Fact]
    public void An_unhandled_exception_keeps_a_known_status_and_hides_every_other()
    {
        Assert.Equal(
            StatusCodes.Status413PayloadTooLarge,
            ProblemResults.StatusForException(new BadHttpRequestException("too big", StatusCodes.Status413PayloadTooLarge)));
        Assert.Equal(
            StatusCodes.Status409Conflict,
            ProblemResults.StatusForException(new DbUpdateConcurrencyException("row moved")));
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            ProblemResults.StatusForException(new InvalidOperationException("anything else")));
    }
}
