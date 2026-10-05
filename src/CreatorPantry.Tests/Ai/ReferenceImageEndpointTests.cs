using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation.Results;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// IMG-004's request contract: what a client may ask for, and what it cannot reach.
/// </summary>
public sealed class ReferenceImageEndpointTests
{
    /// <summary>
    /// The whole contract is an uploaded document and an optional note.
    /// </summary>
    /// <remarks>
    /// Asserted as an exact set rather than field by field, because the risk worth catching is an
    /// <em>addition</em>: a media type, a byte array or a dimension here would be a second upload surface
    /// with its own inspection to keep in step with the brand module's.
    /// </remarks>
    [Fact]
    public void The_request_carries_an_uploaded_document_and_a_note()
    {
        var fields = typeof(RequestReferenceImageAnalysisViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Note", "ReferenceDocumentId"], fields);
    }

    /// <summary>
    /// The contract cannot carry bytes, a declared type, or a rendering decision.
    /// </summary>
    /// <remarks>
    /// This is the spoofed-MIME case closed by construction: the type the handler trusts is the one the
    /// inspector established from the file's own bytes at upload, and a client has no field to declare a
    /// different one in.
    /// </remarks>
    [Theory]
    [InlineData("media")]
    [InlineData("mime")]
    [InlineData("contentType")]
    [InlineData("fileName")]
    [InlineData("bytes")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("seed")]
    [InlineData("steps")]
    [InlineData("sampler")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("workspace")]
    [InlineData("schema")]
    public void The_request_cannot_name_a_file_fact_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestReferenceImageAnalysisViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An `observations` or `prompt` field would make the request the answer.
    /// </summary>
    [Fact]
    public void The_request_cannot_carry_the_answer()
    {
        var names = typeof(RequestReferenceImageAnalysisViewModel).GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("Observations", names);
        Assert.DoesNotContain("Prompt", names);
        Assert.DoesNotContain("Avoid", names);
        Assert.DoesNotContain("Confidence", names);
    }

    [Fact]
    public void An_uploaded_document_is_enough()
    {
        var result = Validate(Minimal());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Fact]
    public void A_request_naming_no_image_is_refused()
    {
        var model = Minimal();
        model.ReferenceDocumentId = Guid.Empty;

        var result = Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "ReferenceDocumentId");
    }

    [Fact]
    public void A_note_past_its_length_is_refused()
    {
        var model = Minimal();
        model.Note = new string('a', AiPolicy.PhotographyCreatorConceptMaxLength + 1);

        var result = Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "Note");
    }

    [Fact]
    public void A_note_is_optional()
    {
        var model = Minimal();
        model.Note = null;

        Assert.True(Validate(model).IsValid);
    }

    [Fact]
    public void The_task_requires_its_own_request_contract()
    {
        Assert.True(AiTaskCatalog.RequiresTaskInputs(AiTaskType.ReferenceImageAnalysis));
        Assert.Equal(
            AiTaskType.ReferenceImageAnalysis,
            AiTaskCatalog.Resolve(AiTaskCatalog.ReferenceImageAnalysis));
    }

    /// <summary>
    /// Nothing can apply a reading of a photograph to a recipe.
    /// </summary>
    /// <remarks>
    /// IMG-004 changes no recipe (its SCOPE), and absence from <c>AiChangeApplicability</c> is how that is
    /// enforced rather than promised: there is no code path from a stored row to a recipe edit.
    /// </remarks>
    [Theory]
    [InlineData(AiChangeKind.Add)]
    [InlineData(AiChangeKind.Set)]
    [InlineData(AiChangeKind.Remove)]
    [InlineData(AiChangeKind.Move)]
    public void A_reading_is_never_applicable_to_a_recipe(AiChangeKind kind)
    {
        Assert.False(
            AiChangeApplicability.IsApplicable(kind, AiChangeTargetKind.ReferenceImageAnalysis));
    }

    private static RequestReferenceImageAnalysisViewModel Minimal() => new()
    {
        ReferenceDocumentId = Guid.NewGuid(),
        Note = "I want the same quiet, overcast feel as this one.",
    };

    private static ValidationResult Validate(RequestReferenceImageAnalysisViewModel model) =>
        new RequestReferenceImageAnalysisViewModelValidator().Validate(model);
}
