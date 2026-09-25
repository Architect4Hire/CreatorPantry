using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// Assembling the stored diff, and the staleness rule that decides whether it may be stored at all.
/// </summary>
public sealed class AiProposalAssemblerTests
{
    private static readonly Guid Workspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Operation = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid PinnedVersion = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // ---- stale source ----------------------------------------------------------------------------------

    /// <summary>
    /// The restriction this stage exists to enforce. Every before value and every target id was read from the
    /// pinned version, so applying the diff to a newer one would apply it to content it was never computed
    /// from. Failing is the only honest answer; re-requesting is the creator's choice to make.
    /// </summary>
    [Fact]
    public void A_recipe_that_moved_on_refuses_to_store_the_diff()
    {
        var assembly = Assemble(currentVersionId: Guid.NewGuid());

        Assert.False(assembly.Succeeded);
        Assert.Null(assembly.Proposal);
        Assert.Equal(AiOutputReason.SourceChanged, assembly.Failure!.ReasonCode);
        Assert.Equal(AiFailureCategory.DomainInvalid, assembly.Failure.Category);
    }

    /// <summary>Re-asking cannot fix a stale source: the answer was fine, the world moved.</summary>
    [Fact]
    public void A_stale_source_is_not_worth_re_asking_for()
    {
        Assert.False(Assemble(currentVersionId: Guid.NewGuid()).Failure!.IsCorrectableByReprompt);
    }

    [Fact]
    public void A_source_that_is_still_current_assembles()
    {
        var assembly = Assemble(currentVersionId: PinnedVersion);

        Assert.True(assembly.Succeeded, assembly.Failure?.Message);
        Assert.Equal(PinnedVersion, assembly.Proposal!.SourceRecipeVersionId);
    }

    /// <summary>A task that is not version-specific pins nothing, and nothing can therefore go stale.</summary>
    [Fact]
    public void A_proposal_with_no_pinned_version_is_not_stale()
    {
        var assembly = Assemble(pinnedVersionId: null, currentVersionId: null);

        Assert.True(assembly.Succeeded, assembly.Failure?.Message);
    }

    /// <summary>
    /// A version arriving where none was pinned is still a change. Failing closed matters more than being
    /// clever about which direction the difference runs in.
    /// </summary>
    [Fact]
    public void A_version_appearing_where_none_was_pinned_is_stale()
    {
        var assembly = Assemble(pinnedVersionId: null, currentVersionId: Guid.NewGuid());

        Assert.False(assembly.Succeeded);
        Assert.Equal(AiOutputReason.SourceChanged, assembly.Failure!.ReasonCode);
    }

    // ---- the stored diff -------------------------------------------------------------------------------

    [Fact]
    public void Every_resolved_change_becomes_a_pending_row()
    {
        var assembly = Assemble(currentVersionId: PinnedVersion);
        var changes = assembly.Proposal!.Changes.OrderBy(change => change.SortOrder).ToList();

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));
        Assert.All(changes, change => Assert.Equal(Workspace, change.WorkspaceId));

        // The decision columns move together with the disposition, so a pending row has neither.
        Assert.All(changes, change => Assert.Null(change.DecidedAt));
        Assert.All(changes, change => Assert.Null(change.DecidedByMembershipId));
    }

    /// <summary>
    /// The before value on the row is the one the diff read from the snapshot. Nothing at this stage can
    /// substitute a model's claim, because the answer never carried one.
    /// </summary>
    [Fact]
    public void The_stored_before_value_is_the_one_the_diff_computed()
    {
        var assembly = Assemble(currentVersionId: PinnedVersion);

        var headnote = assembly.Proposal!.Changes.Single(change => change.FieldName == "headnote");

        Assert.Equal("The original headnote.", headnote.BeforeValue);
        Assert.Equal("A warmer opening.", headnote.AfterValue);
    }

    [Fact]
    public void Provenance_is_recorded_on_the_proposal()
    {
        var proposal = Assemble(currentVersionId: PinnedVersion).Proposal!;

        Assert.Equal("fixture.concepts.v1", proposal.OutputSchemaVersion);
        Assert.Equal("fixture.concepts", proposal.PromptTemplateId);
        Assert.Equal("1.2.0", proposal.PromptTemplateVersion);
        Assert.Equal("sha256:abc123", proposal.PromptTemplateBodyChecksum);
        Assert.Equal("test-provider", proposal.ProviderName);
        Assert.Equal("test-model", proposal.ModelName);
        Assert.Equal(Now, proposal.CreatedAt);
    }

    // ---- warnings --------------------------------------------------------------------------------------

    /// <summary>
    /// A warning names its change by index in the answer; the row names it by id. The diff preserves the
    /// answer's order as SortOrder, which is what makes the index resolvable to exactly one change.
    /// </summary>
    [Fact]
    public void A_warning_about_a_change_is_attached_to_that_change()
    {
        var assembly = Assemble(currentVersionId: PinnedVersion);

        var warning = assembly.Proposal!.Warnings.Single(candidate => candidate.AiStructuredChangeId is not null);
        var expected = assembly.Proposal.Changes.Single(change => change.SortOrder == 1);

        Assert.Equal(expected.Id, warning.AiStructuredChangeId);
    }

    [Fact]
    public void A_warning_about_the_whole_answer_is_attached_to_nothing()
    {
        var assembly = Assemble(currentVersionId: PinnedVersion);

        Assert.Contains(
            assembly.Proposal!.Warnings,
            warning => warning.Kind is AiWarningKind.SafetyCaution && warning.AiStructuredChangeId is null);
    }

    [Fact]
    public void A_warning_index_that_resolves_to_nothing_attaches_to_nothing()
    {
        var assembly = AiProposalAssembler.Assemble(
            Workspace,
            Operation,
            PinnedVersion,
            PinnedVersion,
            new AiOutputDocument
            {
                SchemaVersion = "fixture.concepts.v1",
                Warnings =
                [
                    new AiOutputWarning
                    {
                        Kind = AiWarningKind.Assumption,
                        Message = "About a change that is not here.",
                        ChangeIndex = 7,
                    },
                ],
            },
            [],
            Provenance,
            Now);

        Assert.True(assembly.Succeeded, assembly.Failure?.Message);
        Assert.Null(Assert.Single(assembly.Proposal!.Warnings).AiStructuredChangeId);
    }

    /// <summary>"Nothing needs changing" stores as a proposal with warnings and no changes.</summary>
    [Fact]
    public void An_empty_diff_still_assembles()
    {
        var assembly = AiProposalAssembler.Assemble(
            Workspace,
            Operation,
            PinnedVersion,
            PinnedVersion,
            new AiOutputDocument
            {
                SchemaVersion = "fixture.concepts.v1",
                Warnings =
                [
                    new AiOutputWarning
                    {
                        Kind = AiWarningKind.Assumption,
                        Message = "The recipe already reads well.",
                    },
                ],
            },
            [],
            Provenance,
            Now);

        Assert.True(assembly.Succeeded, assembly.Failure?.Message);
        Assert.Empty(assembly.Proposal!.Changes);
        Assert.Single(assembly.Proposal.Warnings);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static readonly AiProposalProvenance Provenance = new(
        "fixture.concepts.v1",
        "fixture.concepts",
        "1.2.0",
        "sha256:abc123",
        "test-provider",
        "test-model",
        null);

    private static AiProposalAssembly Assemble(Guid? currentVersionId, Guid? pinnedVersionId = null) =>
        AiProposalAssembler.Assemble(
            Workspace,
            Operation,
            pinnedVersionId ?? (currentVersionId is null ? null : PinnedVersion),
            currentVersionId,
            new AiOutputDocument
            {
                SchemaVersion = "fixture.concepts.v1",
                Warnings =
                [
                    new AiOutputWarning
                    {
                        Kind = AiWarningKind.Assumption,
                        Message = "Kept the yield as written.",
                        ChangeIndex = 1,
                    },
                    new AiOutputWarning
                    {
                        Kind = AiWarningKind.SafetyCaution,
                        Message = "Storage advice is descriptive, not a guarantee.",
                    },
                ],
            },
            [
                new AiResolvedChange(
                    AiChangeKind.Set,
                    AiChangeTargetKind.Recipe,
                    null,
                    "headnote",
                    "The original headnote.",
                    "A warmer opening.",
                    null,
                    0),
                new AiResolvedChange(
                    AiChangeKind.Set,
                    AiChangeTargetKind.Recipe,
                    null,
                    "title",
                    "Focaccia",
                    "Rosemary Focaccia",
                    null,
                    1),
            ],
            Provenance,
            Now);
}
