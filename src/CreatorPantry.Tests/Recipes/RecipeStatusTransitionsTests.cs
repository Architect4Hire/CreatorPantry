using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The shape of the editorial state machine itself (TESTRUN-005): which moves exist, which do not, and what
/// each one asks for.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and exhaustive over every ordered pair of states. That matters more than testing the moves one at a
/// time would: "invalid jumps fail" is a claim about the moves that are <em>absent</em>, and the only way to
/// check an absence is to enumerate the whole space and assert what is not in it. A rule added by mistake
/// fails <see cref="Every_pair_is_either_a_listed_move_or_no_move_at_all"/> rather than passing quietly
/// because nobody wrote a test for a transition nobody intended.
/// </para>
/// <para>
/// What the machine <em>does</em> when a move is legal is <c>RecipeTransitionBusinessTests</c>; that it
/// commits atomically is <c>RecipeDataLayerTests</c>.
/// </para>
/// </remarks>
public sealed class RecipeStatusTransitionsTests
{
    private static readonly RecipeStatus[] AllStates = Enum.GetValues<RecipeStatus>();

    /// <summary>
    /// The machine, written out independently of the implementation. Every legal move and nothing else, so
    /// that a change to <see cref="RecipeStatusTransitions.All"/> has to be made here too — which is the
    /// point: this is the matrix a reviewer approved, and the test is what makes changing it deliberate.
    /// </summary>
    private static readonly (RecipeStatus From, RecipeStatus To, WorkspaceRole Role)[] Expected =
    [
        (RecipeStatus.Draft, RecipeStatus.InDevelopment, WorkspaceRole.Contributor),
        (RecipeStatus.InDevelopment, RecipeStatus.Testing, WorkspaceRole.Contributor),
        (RecipeStatus.Testing, RecipeStatus.ReadyForReview, WorkspaceRole.Contributor),
        (RecipeStatus.ReadyForReview, RecipeStatus.Approved, WorkspaceRole.Editor),
        (RecipeStatus.Testing, RecipeStatus.InDevelopment, WorkspaceRole.Editor),
        (RecipeStatus.ReadyForReview, RecipeStatus.InDevelopment, WorkspaceRole.Editor),
        (RecipeStatus.Approved, RecipeStatus.InDevelopment, WorkspaceRole.Editor),
        (RecipeStatus.Draft, RecipeStatus.Archived, WorkspaceRole.Editor),
        (RecipeStatus.InDevelopment, RecipeStatus.Archived, WorkspaceRole.Editor),
        (RecipeStatus.Testing, RecipeStatus.Archived, WorkspaceRole.Editor),
        (RecipeStatus.ReadyForReview, RecipeStatus.Archived, WorkspaceRole.Editor),
        (RecipeStatus.Approved, RecipeStatus.Archived, WorkspaceRole.Editor),
        (RecipeStatus.Archived, RecipeStatus.Draft, WorkspaceRole.Editor),
    ];

    /// <summary>
    /// Every ordered pair of states: a listed move with the role it was approved at, or no move.
    /// </summary>
    [Fact]
    public void Every_pair_is_either_a_listed_move_or_no_move_at_all()
    {
        var expected = Expected.ToDictionary(move => (move.From, move.To), move => move.Role);

        foreach (var from in AllStates)
        {
            foreach (var to in AllStates)
            {
                var rule = RecipeStatusTransitions.Find(from, to);

                if (expected.TryGetValue((from, to), out var role))
                {
                    Assert.NotNull(rule);
                    Assert.Equal(role, rule.MinimumRole);
                }
                else
                {
                    Assert.Null(rule);
                }
            }
        }
    }

    /// <summary>
    /// A state to itself is never a move. Not an invalid jump — a jump goes somewhere — but a repeat, which
    /// Business answers as a no-op; there is nothing here for it to find.
    /// </summary>
    [Fact]
    public void No_state_transitions_to_itself() =>
        Assert.All(AllStates, state => Assert.Null(RecipeStatusTransitions.Find(state, state)));

    /// <summary>
    /// Forward progress is one step at a time. Each of these skips a state, and each is a claim about work
    /// that nobody did.
    /// </summary>
    [Theory]
    [InlineData(RecipeStatus.Draft, RecipeStatus.Testing)]
    [InlineData(RecipeStatus.Draft, RecipeStatus.ReadyForReview)]
    [InlineData(RecipeStatus.Draft, RecipeStatus.Approved)]
    [InlineData(RecipeStatus.InDevelopment, RecipeStatus.ReadyForReview)]
    [InlineData(RecipeStatus.InDevelopment, RecipeStatus.Approved)]
    [InlineData(RecipeStatus.Testing, RecipeStatus.Approved)]
    public void A_forward_jump_over_a_state_is_not_a_move(RecipeStatus from, RecipeStatus to) =>
        Assert.Null(RecipeStatusTransitions.Find(from, to));

    /// <summary>
    /// The archive is reached from everywhere and left only to Draft. Restoring into the middle of the
    /// workflow would be a claim about where the recipe had got to, which nothing records —
    /// <see cref="RecipePolicy.UnarchivedStatus"/> is where that decision lives.
    /// </summary>
    [Fact]
    public void The_archive_is_entered_from_every_state_and_left_only_to_draft()
    {
        Assert.Equal(
            [.. AllStates.Where(state => state != RecipeStatus.Archived)],
            [.. RecipeStatusTransitions.All
                .Where(rule => rule.To == RecipeStatus.Archived)
                .Select(rule => rule.From)
                .Order()]);

        Assert.Equal([RecipePolicy.UnarchivedStatus], RecipeStatusTransitions.From(RecipeStatus.Archived));
    }

    /// <summary>
    /// A reopen goes to InDevelopment from every state past it, and is the only move that requires a reason.
    /// </summary>
    [Fact]
    public void Every_reopen_targets_in_development_and_requires_a_reason()
    {
        var reopens = RecipeStatusTransitions.All.Where(rule => rule.RequiresReason).ToList();

        Assert.All(reopens, rule => Assert.Equal(RecipeStatusTransitions.ReopenTarget, rule.To));

        // In the catalogue's own order, which is workflow order — the order a reader of the list needs.
        Assert.Equal(
            [RecipeStatus.Testing, RecipeStatus.ReadyForReview, RecipeStatus.Approved],
            [.. reopens.Select(rule => rule.From)]);
    }

    /// <summary>
    /// Exactly one move is gated on readiness and exactly one writes a version, and they are the same move.
    /// </summary>
    /// <remarks>
    /// Asserted together because the pairing is the point: a gate with no version would approve content that
    /// can then change, and a version with no gate would record an approval nothing checked.
    /// </remarks>
    [Fact]
    public void Only_the_approval_is_gated_and_only_the_approval_writes_a_version()
    {
        var gated = Assert.Single(RecipeStatusTransitions.All, rule => rule.RequiresReadinessClear);
        var versioned = Assert.Single(RecipeStatusTransitions.All, rule => rule.WritesVersion);

        Assert.Same(gated, versioned);
        Assert.Equal(RecipeStatus.ReadyForReview, gated.From);
        Assert.Equal(RecipeStatus.Approved, gated.To);
    }

    /// <summary>
    /// The approval is the only move above Contributor that is not an Editor's, and every move that undoes or
    /// shelves somebody's work is an Editor's. Stated as a property rather than per rule, so a move added at
    /// the wrong bar fails here.
    /// </summary>
    [Fact]
    public void Advancing_is_a_contributors_move_and_everything_else_is_an_editors()
    {
        foreach (var rule in RecipeStatusTransitions.All)
        {
            var advancing = rule is { RequiresReason: false }
                && rule.To != RecipeStatus.Archived
                && rule.From != RecipeStatus.Archived
                && rule.To != RecipeStatus.Approved;

            Assert.Equal(
                advancing ? WorkspaceRole.Contributor : WorkspaceRole.Editor,
                rule.MinimumRole);
        }
    }

    /// <summary>
    /// Every state can be left, and every state but Draft can be reached. A state with no way in or out
    /// would be a trap or a label.
    /// </summary>
    [Fact]
    public void Every_state_is_reachable_and_escapable()
    {
        Assert.All(AllStates, state => Assert.NotEmpty(RecipeStatusTransitions.From(state)));

        var reachable = RecipeStatusTransitions.All.Select(rule => rule.To).Distinct().ToList();

        Assert.All(
            AllStates.Where(state => state != RecipeStatus.Draft),
            state => Assert.Contains(state, reachable));

        // Draft is reachable too, out of the archive — it is simply not reachable from the workflow, because
        // a recipe being worked on does not go back to never having been started.
        Assert.Contains(RecipeStatus.Draft, reachable);
    }

    /// <summary>
    /// Only an edit to an approved recipe reopens it. The earlier states assert that work is underway rather
    /// than that content was cleared, and editing a recipe while testing it is how a test kitchen runs.
    /// </summary>
    [Fact]
    public void An_edit_reopens_an_approved_recipe_and_nothing_else() =>
        Assert.All(
            AllStates,
            state => Assert.Equal(
                state is RecipeStatus.Approved,
                RecipeStatusTransitions.EditReopens(state)));

    /// <summary>
    /// The reopen an edit triggers is a listed move, with a reason supplied — so the machine's own rule for it
    /// is satisfied rather than bypassed.
    /// </summary>
    [Fact]
    public void The_edit_driven_reopen_is_a_move_the_machine_has()
    {
        var rule = RecipeStatusTransitions.Find(RecipeStatus.Approved, RecipeStatusTransitions.ReopenTarget);

        Assert.NotNull(rule);
        Assert.True(rule.RequiresReason);
        Assert.False(string.IsNullOrWhiteSpace(RecipeStatusTransitions.EditReopenReason));
    }

    /// <summary>The same pair never appears twice, which a lookup keyed on it would throw over anyway.</summary>
    [Fact]
    public void No_move_is_listed_twice() =>
        Assert.Equal(
            RecipeStatusTransitions.All.Count,
            RecipeStatusTransitions.All.Select(rule => (rule.From, rule.To)).Distinct().Count());
}
