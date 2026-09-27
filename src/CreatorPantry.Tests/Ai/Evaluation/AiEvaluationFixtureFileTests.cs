namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// What a fixture file has to get right to load at all. Mirrors
/// <c>CreatorPantry.Tests.Prompts.PromptTemplateFileTests</c>, one case per failure branch, for the sibling
/// loader this one is modeled on.
/// </summary>
public sealed class AiEvaluationFixtureFileTests
{
    private const string ResourceName =
        "CreatorPantry.Tests.Ai.Evaluation.Fixtures.SchemaValidity.foundation.fixture-1.0.0.eval.json";

    [Fact]
    public void A_sound_file_parses_into_its_declared_contract()
    {
        var fixture = AiEvaluationFixtureFile.Parse(ResourceName, Content());

        Assert.Equal("foundation.fixture", fixture.Id);
        Assert.Equal(new CreatorPantry.Domain.Managers.Prompts.PromptTemplateVersion(1, 0, 0), fixture.Version);
        Assert.Equal(AiEvaluationCategory.SchemaValidity, fixture.Category);
        Assert.Equal(AiEvaluationKind.OutputValidation, fixture.Kind);
        Assert.Equal("A sound fixture.", fixture.Description);
    }

    [Fact]
    public void Malformed_json_is_refused()
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, "{ not json"));

        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_null_is_refused()
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, "null"));

        Assert.Contains("null", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mistyped_property_is_refused()
    {
        // "categry" rather than "category" -- caught by UnmappedMemberHandling.Disallow rather than being
        // silently ignored and reported as a missing required field, which would point at the wrong problem.
        var failure = Assert.Throws<AiEvaluationException>(() => AiEvaluationFixtureFile.Parse(
            ResourceName,
            Content(extraTopLevel: "\"categry\": \"SchemaValidity\",")));

        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "id")]
    [InlineData("", "id")]
    public void A_missing_id_is_refused(string? id, string mention)
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, Content(id: id)));

        Assert.Contains(mention, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    public void A_malformed_version_is_refused(string? version)
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, Content(version: version)));

        Assert.Contains("version", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_category_is_refused()
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, Content(category: null)));

        Assert.Contains("category", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_kind_is_refused()
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, Content(kind: null)));

        Assert.Contains("kind", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_missing_description_is_refused(string? description)
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, Content(description: description)));

        Assert.Contains("description", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_input_is_refused()
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, Content(includeInput: false)));

        Assert.Contains("input", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_expect_is_refused()
    {
        var failure = Assert.Throws<AiEvaluationException>(() =>
            AiEvaluationFixtureFile.Parse(ResourceName, Content(includeExpect: false)));

        Assert.Contains("expect", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filename_that_disagrees_with_the_manifest_is_refused()
    {
        var failure = Assert.Throws<AiEvaluationException>(() => AiEvaluationFixtureFile.Parse(
            "CreatorPantry.Tests.Ai.Evaluation.Fixtures.SchemaValidity.foundation.fixture-2.0.0.eval.json",
            Content()));

        Assert.Contains("file name", failure.Message, StringComparison.Ordinal);
    }

    private static string Content(
        string? id = "foundation.fixture",
        string? version = "1.0.0",
        string? category = "SchemaValidity",
        string? kind = "OutputValidation",
        string? description = "A sound fixture.",
        bool includeInput = true,
        bool includeExpect = true,
        string extraTopLevel = "")
    {
        var fields = new List<string> { extraTopLevel };

        if (id is not null)
        {
            fields.Add($"\"id\": {Quote(id)}");
        }

        if (version is not null)
        {
            fields.Add($"\"version\": {Quote(version)}");
        }

        if (category is not null)
        {
            fields.Add($"\"category\": {Quote(category)}");
        }

        if (kind is not null)
        {
            fields.Add($"\"kind\": {Quote(kind)}");
        }

        if (description is not null)
        {
            fields.Add($"\"description\": {Quote(description)}");
        }

        if (includeInput)
        {
            fields.Add("\"input\": {}");
        }

        if (includeExpect)
        {
            fields.Add("\"expect\": {}");
        }

        return "{ " + string.Join(", ", fields.Where(field => field.Length > 0)) + " }";
    }

    private static string Quote(string value) => $"\"{value}\"";
}
