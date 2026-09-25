using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The shape of <see cref="AiOperation"/> in the built EF model.
/// </summary>
/// <remarks>
/// Borrows <c>RecipeAggregateFixture</c> rather than standing up a second one: it builds the whole
/// <c>CreatorPantryDbContext</c> model, which is all these assertions read, and the AI operation's foreign
/// keys point into the recipe module anyway.
/// </remarks>
public sealed class AiOperationModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Is_workspace_owned_and_filtered()
    {
        var entityType = Operations();

        Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AiOperation)));
        Assert.False(entityType.FindProperty(nameof(AiOperation.WorkspaceId))!.IsNullable);
        Assert.NotEmpty(entityType.GetDeclaredQueryFilters());
    }

    [Fact]
    public void Carries_the_alternate_key_the_proposal_entities_will_point_at()
    {
        var alternateKey = Operations().GetKeys().Single(key => !key.IsPrimaryKey());

        Assert.Equal(
            [nameof(AiOperation.WorkspaceId), nameof(AiOperation.Id)],
            alternateKey.Properties.Select(property => property.Name));
    }

    /// <summary>
    /// One key, one operation — what stops a replay arriving after the generic idempotency record expired
    /// from starting a second run of work the creator already paid for.
    /// </summary>
    [Fact]
    public void Idempotency_key_is_unique_within_the_workspace()
    {
        Assert.Contains(Operations().GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AiOperation.WorkspaceId), nameof(AiOperation.IdempotencyKey)]));
    }

    /// <summary>
    /// The queue-claim seek, and the one index in the schema that deliberately does not lead with
    /// <c>WorkspaceId</c>: a worker looks for queued work before it knows which workspace it is about to
    /// serve. Asserted explicitly so that "not workspace-leading" stays a decision rather than an oversight
    /// someone later corrects into uselessness.
    /// </summary>
    [Fact]
    public void The_claim_index_leads_with_status_not_workspace()
    {
        // AvailableAt rather than RequestedAt: a requeued operation is deliberately not claimable until its
        // backoff has passed, so that is the predicate the seek has to answer.
        var claim = Operations().GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AiOperation.Status), nameof(AiOperation.AvailableAt)]));

        Assert.False(claim.IsUnique);
    }

    /// <summary>The lease-recovery sweep: running operations whose claim has lapsed.</summary>
    [Fact]
    public void The_recovery_index_covers_lapsed_leases()
    {
        Assert.Contains(Operations().GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AiOperation.Status), nameof(AiOperation.LeaseExpiresAt)]));
    }

    [Fact]
    public void A_recipe_delete_cannot_reach_its_ai_history()
    {
        var operations = Operations();

        Assert.Equal(
            DeleteBehavior.Restrict,
            operations.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(Recipe))
                .DeleteBehavior);

        Assert.Equal(
            DeleteBehavior.Restrict,
            operations.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(RecipeVersion))
                .DeleteBehavior);
    }

    /// <summary>Erasing a workspace still erases its AI history; that cascade is deliberate.</summary>
    [Fact]
    public void A_workspace_delete_reaches_its_operations()
    {
        Assert.Equal(
            DeleteBehavior.Cascade,
            Operations().GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(Workspace))
                .DeleteBehavior);
    }

    /// <summary>
    /// Both recipe references carry <c>WorkspaceId</c> into the key, so an operation pointing at another
    /// workspace's recipe is unreferenceable at the database rather than only refused by the code above it.
    /// </summary>
    [Fact]
    public void Both_recipe_references_are_composite_with_the_workspace()
    {
        var crossModule = Operations().GetForeignKeys()
            .Where(key => key.PrincipalEntityType.ClrType == typeof(Recipe)
                || key.PrincipalEntityType.ClrType == typeof(RecipeVersion));

        Assert.All(crossModule, key =>
            Assert.Equal(nameof(AiOperation.WorkspaceId), key.Properties[0].Name));
    }

    /// <remarks>
    /// Check constraints are read from the design-time model. The runtime model is read-optimized and drops
    /// them — it throws rather than returning an empty list, which is the right call and worth knowing about
    /// before writing the next schema assertion.
    /// </remarks>
    [Theory]
    [InlineData("CK_AiOperations_Failure_Status")]
    [InlineData("CK_AiOperations_TaskType_Declared")]
    [InlineData("CK_AiOperations_Scope_Declared")]
    [InlineData("CK_AiOperations_FailureCategory_Declared")]
    [InlineData("CK_AiOperations_Running_HasStarted")]
    [InlineData("CK_AiOperations_Completed_Terminal")]
    [InlineData("CK_AiOperations_Version_Requires_Recipe")]
    public void Declares_its_check_constraints(string name)
    {
        Assert.Contains(DesignTimeOperations().GetCheckConstraints(), constraint => constraint.Name == name);
    }

    /// <summary>
    /// The database's idea of which states are final and the state machine's must be the same idea. The
    /// constraint is generated from <see cref="AiOperationTransitionPolicy.TerminalStatuses"/>, and this is
    /// what proves the generation actually happened rather than someone having typed the list twice.
    /// </summary>
    [Fact]
    public void The_terminal_check_constraint_matches_the_transition_policy()
    {
        var constraint = DesignTimeOperations().GetCheckConstraints()
            .Single(check => check.Name == "CK_AiOperations_Completed_Terminal");

        var expected = string.Join(
            ", ",
            AiOperationTransitionPolicy.TerminalStatuses.Select(status => (int)status).Order());

        Assert.Contains($"Status IN ({expected})", constraint.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// ai.md forbids storing private prompt bodies and generated creator content by default. This table has
    /// nowhere to put either — the only text column is the caller's idempotency key — and that is a property
    /// worth asserting rather than trusting, because the cheapest way to break it is to add a "Notes" or
    /// "LastError" string when something needs debugging.
    /// </summary>
    [Fact]
    public void Has_no_free_text_column_a_prompt_body_could_land_in()
    {
        var textProperties = Operations().GetProperties()
            .Where(property => property.ClrType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal([nameof(AiOperation.IdempotencyKey)], textProperties);
    }

    [Fact]
    public void Idempotency_key_is_required_and_bounded()
    {
        var key = Operations().FindProperty(nameof(AiOperation.IdempotencyKey))!;

        Assert.False(key.IsNullable);
        Assert.Equal(AiPolicy.IdempotencyKeyMaxLength, key.GetMaxLength());
    }

    private IEntityType Operations()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(AiOperation))!;
    }

    private IEntityType DesignTimeOperations()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var model = RecipeAggregateFixture.Db(scope).GetService<IDesignTimeModel>().Model;

        return model.FindEntityType(typeof(AiOperation))!;
    }
}
