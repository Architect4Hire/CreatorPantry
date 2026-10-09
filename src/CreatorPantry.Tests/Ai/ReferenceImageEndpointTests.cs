using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation.Results;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// IMG-004's request contract: what a client may ask for, and what it cannot reach.
/// </summary>
public sealed class ReferenceImageEndpointTests
{
    /// <summary>
    /// The whole contract is which picture — a source and that source's ids — and an optional note.
    /// </summary>
    /// <remarks>
    /// Asserted as an exact set rather than field by field, because the risk worth catching is an
    /// <em>addition</em>: a media type, a byte array or a dimension here would be a second upload surface
    /// with its own inspection to keep in step with the modules that hold the pictures. AF.3.4 added the
    /// source and the ids of the two new kinds of picture, and nothing about any picture's bytes.
    /// </remarks>
    [Fact]
    public void The_request_carries_one_named_picture_and_a_note()
    {
        var fields = typeof(RequestReferenceImageAnalysisViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["GeneratedImageId", "MediaAssetId", "MediaAssetVersionNumber", "Note", "ReferenceDocumentId", "Source"],
            fields);
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
    [InlineData("mediaType")]
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
    public void A_request_with_no_source_and_only_a_document_is_a_brand_document()
    {
        // Every request made before there was a choice of source.
        var model = new RequestReferenceImageAnalysisViewModel { ReferenceDocumentId = Guid.NewGuid() };

        Assert.Equal(AiReferenceImageSource.BrandDocument, AiReferenceImageSources.Of(model));
        Assert.True(Validate(model).IsValid);
    }

    [Fact]
    public void Each_source_is_valid_with_its_own_ids_and_no_others()
    {
        RequestReferenceImageAnalysisViewModel[] valid =
        [
            new() { Source = AiReferenceImageSource.BrandDocument, ReferenceDocumentId = Guid.NewGuid() },
            new() { Source = AiReferenceImageSource.DamAsset, MediaAssetId = Guid.NewGuid() },
            new() { Source = AiReferenceImageSource.DamAsset, MediaAssetId = Guid.NewGuid(), MediaAssetVersionNumber = 3 },
            new() { Source = AiReferenceImageSource.GeneratedImage, GeneratedImageId = Guid.NewGuid() },
        ];

        Assert.All(valid, model =>
        {
            var result = Validate(model);
            Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
        });
    }

    [Fact]
    public void A_source_without_its_id_is_refused_on_that_id()
    {
        Assert.Contains(
            Validate(new() { Source = AiReferenceImageSource.DamAsset }).Errors,
            error => error.PropertyName == "MediaAssetId");
        Assert.Contains(
            Validate(new() { Source = AiReferenceImageSource.GeneratedImage }).Errors,
            error => error.PropertyName == "GeneratedImageId");
        Assert.Contains(
            Validate(new() { Source = AiReferenceImageSource.BrandDocument }).Errors,
            error => error.PropertyName == "ReferenceDocumentId");
        Assert.Contains(
            Validate(new() { Source = AiReferenceImageSource.DamAsset, MediaAssetId = Guid.Empty }).Errors,
            error => error.PropertyName == "MediaAssetId");
    }

    [Fact]
    public void An_id_that_belongs_to_another_source_is_refused_on_that_id()
    {
        var mixed = Validate(new()
        {
            Source = AiReferenceImageSource.GeneratedImage,
            GeneratedImageId = Guid.NewGuid(),
            MediaAssetId = Guid.NewGuid(),
            MediaAssetVersionNumber = 1,
            ReferenceDocumentId = Guid.NewGuid(),
        });

        Assert.False(mixed.IsValid);
        Assert.Contains(mixed.Errors, error => error.PropertyName == "MediaAssetId");
        Assert.Contains(mixed.Errors, error => error.PropertyName == "MediaAssetVersionNumber");
        Assert.Contains(mixed.Errors, error => error.PropertyName == "ReferenceDocumentId");
        Assert.DoesNotContain(mixed.Errors, error => error.PropertyName == "GeneratedImageId");
    }

    /// <summary>
    /// With no source stated, anything but a lone document id names nothing — it is never guessed.
    /// </summary>
    [Fact]
    public void An_unstated_source_is_never_guessed_from_two_ids_or_from_a_new_kind_of_id()
    {
        RequestReferenceImageAnalysisViewModel[] ambiguous =
        [
            new(),
            new() { MediaAssetId = Guid.NewGuid() },
            new() { GeneratedImageId = Guid.NewGuid() },
            new() { ReferenceDocumentId = Guid.NewGuid(), GeneratedImageId = Guid.NewGuid() },
            new() { ReferenceDocumentId = Guid.NewGuid(), MediaAssetVersionNumber = 1 },
        ];

        Assert.All(ambiguous, model =>
        {
            Assert.Null(AiReferenceImageSources.Of(model));
            Assert.Contains(Validate(model).Errors, error => error.PropertyName == "Source");
        });
    }

    [Fact]
    public void A_source_outside_the_enum_and_a_version_below_one_are_refused()
    {
        Assert.Contains(
            Validate(new() { Source = (AiReferenceImageSource)9, MediaAssetId = Guid.NewGuid() }).Errors,
            error => error.PropertyName == "Source");
        Assert.Contains(
            Validate(new() { Source = AiReferenceImageSource.DamAsset, MediaAssetId = Guid.NewGuid(), MediaAssetVersionNumber = 0 }).Errors,
            error => error.PropertyName == "MediaAssetVersionNumber");
    }

    /// <summary>
    /// An operation queued before AF.3.4 has no source written, and is still a brand document to the worker.
    /// </summary>
    [Fact]
    public void An_operation_with_no_source_written_is_read_as_a_brand_document()
    {
        var before = new Dictionary<string, string> { [ReferenceImageInputs.ReferenceDocumentId] = Guid.NewGuid().ToString() };
        var nonsense = new Dictionary<string, string> { [ReferenceImageInputs.Source] = "Somewhere" };
        var asset = new Dictionary<string, string> { [ReferenceImageInputs.Source] = "DamAsset" };

        Assert.Equal(AiReferenceImageSource.BrandDocument, ReferenceImageInputs.ReadSource(before));
        Assert.Equal(AiReferenceImageSource.BrandDocument, ReferenceImageInputs.ReadSource(nonsense));
        Assert.Equal(AiReferenceImageSource.BrandDocument, ReferenceImageInputs.ReadSource(null));
        Assert.Equal(AiReferenceImageSource.DamAsset, ReferenceImageInputs.ReadSource(asset));
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
