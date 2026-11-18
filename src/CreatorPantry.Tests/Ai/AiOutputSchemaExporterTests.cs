using System.Text.Json.Nodes;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The schema a model is shown is as strict as the validator that judges its answer: every validator refuses an
/// unknown field and a null in a non-nullable place, so every exported schema says so too.
/// </summary>
/// <remarks>
/// Before <see cref="AiOutputSchemaExporter"/>, each schema was exported with the serializer's defaults, which
/// allowed any extra property, a null list item and a null object in a list. A model could follow the schema to
/// the letter and still be refused, spending the request's one correction.
/// </remarks>
public sealed class AiOutputSchemaExporterTests
{
    public static TheoryData<string, string> EverySchema => new()
    {
        { nameof(AiBrandGuideOutputSchema), AiBrandGuideOutputSchema.Json },
        { nameof(AiBrandStyleSamplesOutputSchema), AiBrandStyleSamplesOutputSchema.Json },
        { nameof(AiChannelPostsOutputSchema), AiChannelPostsOutputSchema.Json },
        { nameof(AiConceptOutputSchema), AiConceptOutputSchema.Json },
        { nameof(AiDishFacetsOutputSchema), AiDishFacetsOutputSchema.Json },
        { nameof(AiEditorialPackageOutputSchema), AiEditorialPackageOutputSchema.Json },
        { nameof(AiImagePromptCompactionOutputSchema), AiImagePromptCompactionOutputSchema.Json },
        { nameof(AiImagePromptOutputSchema), AiImagePromptOutputSchema.Json },
        { nameof(AiLongFormOutlineOutputSchema), AiLongFormOutlineOutputSchema.Json },
        { nameof(AiLongFormSectionOutputSchema), AiLongFormSectionOutputSchema.Json },
        { nameof(AiOutputSchema), AiOutputSchema.Json },
        { nameof(AiPhotographyConceptOutputSchema), AiPhotographyConceptOutputSchema.Json },
        { nameof(AiRecipeDraftOutputSchema), AiRecipeDraftOutputSchema.Json },
        { nameof(AiRecipeReviewOutputSchema), AiRecipeReviewOutputSchema.Json },
        { nameof(AiReferenceImageOutputSchema), AiReferenceImageOutputSchema.Json },
        { nameof(AiSeoPackageOutputSchema), AiSeoPackageOutputSchema.Json },
        { nameof(AiSubstitutionOutputSchema), AiSubstitutionOutputSchema.Json },
    };

    [Theory]
    [MemberData(nameof(EverySchema))]
    public void Every_object_in_every_schema_forbids_properties_the_validator_would_refuse(string name, string json)
    {
        var objects = Walk(JsonNode.Parse(json)!).Where(node => node["properties"] is JsonObject).ToList();

        Assert.NotEmpty(objects);
        Assert.All(objects, node => Assert.False(
            node["additionalProperties"]?.GetValue<bool>() ?? true,
            $"{name}: an object schema leaves additionalProperties open."));
    }

    [Theory]
    [MemberData(nameof(EverySchema))]
    public void No_schema_offers_a_null_document_or_a_null_list_item(string name, string json)
    {
        var root = JsonNode.Parse(json)!;

        Assert.Equal("object", root["type"]?.GetValue<string>());

        foreach (var items in Walk(root).Select(node => node["items"]).OfType<JsonObject>())
        {
            Assert.False(
                items["type"] is JsonArray types && types.Any(type => type?.GetValue<string>() == "null"),
                $"{name}: a list offers null items, which its validator refuses.");
        }
    }

    [Theory]
    [MemberData(nameof(EverySchema))]
    public void Every_schema_declares_a_schema_version_for_the_builder_to_pin(string name, string json)
    {
        var version = JsonNode.Parse(json)!["properties"]?["schemaVersion"];

        Assert.True(version is JsonObject, $"{name}: no schemaVersion property to pin.");
        Assert.Contains("schemaVersion", JsonNode.Parse(json)!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    /// <summary>A nullable property stays nullable: strictness is about what the validator refuses, nothing more.</summary>
    [Fact]
    public void A_nullable_property_still_accepts_null()
    {
        var concept = JsonNode.Parse(AiConceptOutputSchema.Json)!["properties"]!["concepts"]!["items"]!;
        var types = concept["properties"]!["timeBudgetNote"]!["type"]!.AsArray().Select(type => type!.GetValue<string>());

        Assert.Contains("null", types);
    }

    private static IEnumerable<JsonObject> Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject value:
                yield return value;

                foreach (var (_, child) in value)
                {
                    if (child is not null)
                    {
                        foreach (var nested in Walk(child))
                        {
                            yield return nested;
                        }
                    }
                }

                break;

            case JsonArray array:
                foreach (var child in array.OfType<JsonNode>())
                {
                    foreach (var nested in Walk(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
