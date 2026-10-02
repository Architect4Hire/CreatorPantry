extern alias ApiService;

using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// 11A.17's request contract: what a client may ask for, what it cannot reach, and the status each refusal
/// becomes.
/// </summary>
/// <remarks>
/// The status mapping is asserted here rather than through HTTP because it is decided by the error code's own
/// suffix (<c>ProblemResults.StatusFor</c>), not by the controller — so the controller's documented statuses and
/// the codes Business actually returns can disagree without anything failing to compile. This is the check that
/// catches it, and it is the shape every other AI request route uses.
/// </remarks>
public sealed class BrandGuideProposalRequestContractTests
{
    /// <summary>
    /// The contract's real content is what is absent. A client names a guide, some dimensions, some channels and
    /// some documents; it cannot name a task, a scope, a prompt, a model, a provider parameter, a tool list, a
    /// workspace, or a version of anything — because the type has nowhere to put one.
    /// </summary>
    [Fact]
    public void The_request_carries_only_the_four_declared_fields()
    {
        var fields = typeof(RequestBrandGuideProposalViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ChannelKeys", "Dimensions", "GuideId", "SourceDocumentIds"], fields);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("workspace")]
    [InlineData("system")]
    [InlineData("version")]
    public void The_request_cannot_name_anything_the_server_decides(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestBrandGuideProposalViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Each refusal reaches the client as the status the route documents. Every code is mapped by its suffix, so
    /// a renamed code silently changes a status unless something asserts the pair.
    /// </summary>
    [Theory]
    [InlineData(nameof(AiBrandGuideProposalRequestErrors.RequestInvalid), 400)]
    [InlineData(nameof(AiBrandGuideProposalRequestErrors.TaskNotEnabled), 400)]
    [InlineData(nameof(AiBrandGuideProposalRequestErrors.ChannelInvalid), 400)]
    [InlineData(nameof(AiBrandGuideProposalRequestErrors.GuideNotFound), 404)]
    [InlineData(nameof(AiBrandGuideProposalRequestErrors.RequestNotFound), 404)]
    [InlineData(nameof(AiBrandGuideProposalRequestErrors.SourceUnprocessable), 422)]
    public void Each_error_code_maps_to_the_status_the_route_documents(string name, int expected)
    {
        var code = (string)typeof(AiBrandGuideProposalRequestErrors).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));
    }

    /// <summary>
    /// The guide refusal and the source refusal say nothing about whether the row exists somewhere else.
    /// </summary>
    /// <remarks>
    /// The two differ from each other — 404 for the guide, 422 for a document — which names the parameter at
    /// fault and is the pattern <c>BrandErrorCodes</c> already follows. What must not differ is one cause from
    /// another <em>within</em> each: a never-issued id and another workspace's id are one code and one message
    /// apiece, which <c>AiBrandGuideProposalRequestBusinessTests</c> asserts against a real database.
    /// </remarks>
    [Fact]
    public void Nothing_in_the_error_codes_distinguishes_a_foreign_row_from_a_missing_one()
    {
        var codes = typeof(AiBrandGuideProposalRequestErrors).GetFields()
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.DoesNotContain(
            codes,
            code => code.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                || code.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                || code.Contains("denied", StringComparison.OrdinalIgnoreCase));
    }

    // ---- validator ----

    [Fact]
    public void A_request_naming_no_guide_is_refused()
    {
        var result = Validate(new RequestBrandGuideProposalViewModel());

        Assert.False(result);
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("VOICE")]
    [InlineData("visual")]
    public void A_known_dimension_is_accepted_whatever_its_case(string dimension)
    {
        Assert.True(Validate(new RequestBrandGuideProposalViewModel
        {
            GuideId = Guid.NewGuid(),
            Dimensions = [dimension],
        }));
    }

    [Fact]
    public void An_unknown_or_repeated_dimension_is_refused()
    {
        Assert.False(Validate(new RequestBrandGuideProposalViewModel
        {
            GuideId = Guid.NewGuid(),
            Dimensions = ["voice", "not-a-dimension"],
        }));

        Assert.False(Validate(new RequestBrandGuideProposalViewModel
        {
            GuideId = Guid.NewGuid(),
            Dimensions = ["voice", "Voice"],
        }));
    }

    [Fact]
    public void More_source_documents_than_the_cap_are_refused_at_the_edge()
    {
        Assert.False(Validate(new RequestBrandGuideProposalViewModel
        {
            GuideId = Guid.NewGuid(),
            SourceDocumentIds =
                [.. Enumerable.Range(0, AiPolicy.BrandGuideMaxSourceDocuments + 1).Select(_ => Guid.NewGuid())],
        }));

        Assert.True(Validate(new RequestBrandGuideProposalViewModel
        {
            GuideId = Guid.NewGuid(),
            SourceDocumentIds =
                [.. Enumerable.Range(0, AiPolicy.BrandGuideMaxSourceDocuments).Select(_ => Guid.NewGuid())],
        }));
    }

    [Fact]
    public void A_repeated_or_empty_source_document_is_refused()
    {
        var repeated = Guid.NewGuid();

        Assert.False(Validate(new RequestBrandGuideProposalViewModel
        {
            GuideId = Guid.NewGuid(),
            SourceDocumentIds = [repeated, repeated],
        }));

        Assert.False(Validate(new RequestBrandGuideProposalViewModel
        {
            GuideId = Guid.NewGuid(),
            SourceDocumentIds = [Guid.Empty],
        }));
    }

    private static bool Validate(RequestBrandGuideProposalViewModel model) =>
        new RequestBrandGuideProposalViewModelValidator().Validate(model).IsValid;
}
