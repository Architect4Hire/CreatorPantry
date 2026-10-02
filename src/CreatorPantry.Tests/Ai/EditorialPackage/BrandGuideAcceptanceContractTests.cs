extern alias ApiService;

using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// 11A.18's decision contract: what a client may decide, what it cannot reach, and the status each refusal
/// becomes.
/// </summary>
/// <remarks>
/// The status mapping is asserted here rather than through HTTP for the reason
/// <see cref="BrandGuideProposalRequestContractTests"/> records: it is decided by the error code's own suffix,
/// so the controller's documented statuses and the codes Business returns can disagree without anything failing
/// to compile.
/// </remarks>
public sealed class BrandGuideAcceptanceContractTests
{
    /// <summary>
    /// The contract's real content is what is absent. A client decides, names items, rewrites wording and leaves
    /// a note; it cannot name a guide, a version, a section key, a channel, a dimension, a citation, a model or a
    /// workspace — because the type has nowhere to put one.
    /// </summary>
    [Fact]
    public void The_decision_carries_only_the_declared_fields()
    {
        var fields = typeof(AcceptBrandGuideProposalViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["AcceptedChangeIds", "ChangeReason", "Comment", "Decision", "Edits", "WasHelpful"],
            fields);
    }

    [Theory]
    [InlineData("guide")]
    [InlineData("version")]
    [InlineData("section")]
    [InlineData("channel")]
    [InlineData("dimension")]
    [InlineData("citation")]
    [InlineData("passage")]
    [InlineData("source")]
    [InlineData("workspace")]
    [InlineData("model")]
    [InlineData("prompt")]
    [InlineData("approve")]
    [InlineData("activate")]
    public void The_decision_cannot_name_anything_the_server_decides(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(AcceptBrandGuideProposalViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A rewrite carries an id and a value, and no field name — an item of guidance is one piece of prose, so
    /// there is exactly one thing a rewrite can mean.
    /// </summary>
    [Fact]
    public void A_rewrite_names_an_item_and_its_text_and_nothing_else()
    {
        var fields = typeof(AiBrandGuideEditViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ChangeId", "Value"], fields);
    }

    [Theory]
    [InlineData(nameof(AiBrandGuideAcceptanceErrors.RequestInvalid), 400)]
    [InlineData(nameof(AiBrandGuideAcceptanceErrors.SelectionInvalid), 400)]
    [InlineData(nameof(AiBrandGuideAcceptanceErrors.ProposalNotFound), 404)]
    [InlineData(nameof(AiBrandGuideAcceptanceErrors.ProposalDecided), 409)]
    [InlineData(nameof(AiBrandGuideAcceptanceErrors.ProposalUnreadable), 422)]
    [InlineData(nameof(AiBrandGuideAcceptanceErrors.AcceptanceForbidden), 403)]
    public void Each_error_code_maps_to_the_status_the_route_documents(string name, int expected)
    {
        var code = (string)typeof(AiBrandGuideAcceptanceErrors).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));
    }

    /// <summary>
    /// The brand module's refusals pass through unchanged, so the route's documented statuses depend on their
    /// suffixes too.
    /// </summary>
    [Theory]
    [InlineData(nameof(BrandErrorCodes.GuideWorkingVersionConflict), 409)]
    [InlineData(nameof(BrandErrorCodes.GuideArchivedConflict), 409)]
    [InlineData(nameof(BrandErrorCodes.GuideVersionLimitExceeded), 400)]
    [InlineData(nameof(BrandErrorCodes.GuideSourceUnprocessable), 422)]
    [InlineData(nameof(BrandErrorCodes.GuideNotFound), 404)]
    [InlineData(nameof(BrandErrorCodes.GuideForbidden), 403)]
    public void A_brand_refusal_passed_through_maps_to_the_status_the_route_documents(string name, int expected)
    {
        var code = (string)typeof(BrandErrorCodes).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));
    }

    // ---- the validator ---------------------------------------------------------------------------------

    [Fact]
    public void A_decision_that_says_nothing_is_refused()
    {
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel()));
    }

    [Theory]
    [InlineData(AiDispositionDecision.AcceptAll)]
    [InlineData(AiDispositionDecision.AcceptSelected)]
    public void An_acceptance_that_names_nothing_is_refused(AiDispositionDecision decision)
    {
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel { Decision = decision }));
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = decision,
            AcceptedChangeIds = [],
        }));
    }

    [Fact]
    public void An_acceptance_naming_the_same_item_twice_is_refused()
    {
        var item = Guid.NewGuid();

        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [item, item],
        }));
    }

    /// <summary>
    /// A rejection that also accepts something is a client that has not decided what it is asking for, and one
    /// of the two readings writes to the creator's guide.
    /// </summary>
    [Fact]
    public void A_rejection_cannot_also_accept_rewrite_or_explain_itself()
    {
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.Reject,
            AcceptedChangeIds = [Guid.NewGuid()],
        }));

        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.Reject,
            Edits = [new AiBrandGuideEditViewModel { ChangeId = Guid.NewGuid(), Value = "Mine." }],
        }));

        // A change reason describes a version, and a rejection writes none.
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.Reject,
            ChangeReason = "Because.",
        }));
    }

    /// <summary>A plain rejection is a perfectly good decision and says nothing else.</summary>
    [Fact]
    public void A_rejection_on_its_own_is_valid()
    {
        Assert.True(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.Reject,
            WasHelpful = false,
            Comment = "Thin on evidence.",
        }));
    }

    /// <summary>
    /// Blank is refused rather than read as "leave this out": there is no telling a deliberate clearing from a
    /// slip, and one of the two readings silently drops guidance the creator reviewed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_rewrite_with_no_words_is_refused(string? value)
    {
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [Guid.NewGuid()],
            Edits = [new AiBrandGuideEditViewModel { ChangeId = Guid.NewGuid(), Value = value }],
        }));
    }

    [Fact]
    public void A_rewrite_longer_than_a_change_may_carry_is_refused()
    {
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [Guid.NewGuid()],
            Edits =
            [
                new AiBrandGuideEditViewModel
                {
                    ChangeId = Guid.NewGuid(),
                    Value = new string('x', AiPolicy.ChangeValueMaxLength + 1),
                },
            ],
        }));
    }

    /// <summary>
    /// A rewrite of a rewrite is a client that has lost track of its own request; one of the two values would be
    /// discarded without saying which.
    /// </summary>
    [Fact]
    public void The_same_item_rewritten_twice_is_refused()
    {
        var item = Guid.NewGuid();

        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [item],
            Edits =
            [
                new AiBrandGuideEditViewModel { ChangeId = item, Value = "First." },
                new AiBrandGuideEditViewModel { ChangeId = item, Value = "Second." },
            ],
        }));
    }

    [Fact]
    public void An_over_long_change_reason_or_comment_is_refused()
    {
        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [Guid.NewGuid()],
            ChangeReason = new string('x', AiPolicy.MessageMaxLength + 1),
        }));

        Assert.False(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [Guid.NewGuid()],
            Comment = new string('x', AiPolicy.MessageMaxLength + 1),
        }));
    }

    /// <summary>
    /// A rewrite naming a change the proposal does not contain is refused, but not here: the validator has no
    /// proposal to check against. <c>AiBrandGuideAcceptanceTests</c> covers it where the answer lives.
    /// </summary>
    [Fact]
    public void A_well_formed_acceptance_with_a_rewrite_is_valid()
    {
        var item = Guid.NewGuid();

        Assert.True(Validate(new AcceptBrandGuideProposalViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [item],
            Edits = [new AiBrandGuideEditViewModel { ChangeId = item, Value = "Dry, and a bit wry." }],
            ChangeReason = "Took the tone suggestion, in my own words.",
        }));
    }

    private static bool Validate(AcceptBrandGuideProposalViewModel model) =>
        new AcceptBrandGuideProposalViewModelValidator().Validate(model).IsValid;
}
