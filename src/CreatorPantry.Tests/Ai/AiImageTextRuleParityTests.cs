using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The three image validators' shared claim ban, run as one corpus past all of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the duplication is allowed to exist, and what keeps it honest.</strong> IMG-001, IMG-002 and
/// IMG-004 each own their own refusals — a shared mutable rule set would couple three capabilities'
/// behaviour to one edit — and each has its own pattern as a result. The cost is drift: a hole closed in one
/// stays open in the other two, which is exactly what happened with "peanut-free" until this test existed.
/// </para>
/// <para>
/// So the corpus below is the part that must behave identically: claims about the food's safety, allergens,
/// nutrition and dietary suitability. Every one of them must be refused by all three.
/// </para>
/// <para>
/// <strong>The divergences are deliberate and are asserted separately.</strong> IMG-004 reads a photograph
/// and has a mood aspect, so "wholesome" is mood language there and a claim in a prompt; and a bare "seed" is
/// a poppy seed there and a render directive in a prompt. Those are stated at the bottom rather than hidden.
/// </para>
/// </remarks>
public sealed class AiImageTextRuleParityTests
{
    /// <summary>
    /// Claims no image capability may make about food, whatever shape its answer takes.
    /// </summary>
    public static TheoryData<string> SharedClaims() =>
    [
        "the loaf is gluten free",
        "the loaf is gluten-free",
        "a peanut-free bake",
        "a peanut free bake",
        "wheat-free and dairy free",
        "an allergen-free kitchen",
        "this is safe to eat",
        "the chicken is cooked through",
        "a healthy, nutritious lunch",
        "low-carb and high protein",
        "certified organic flour",
        "an allergy-friendly spread",
        "a diabetic-friendly dessert",
        "suitable for coeliacs",
        "free from nuts",
        "guaranteed non-GMO",
    ];

    [Theory]
    [MemberData(nameof(SharedClaims))]
    public void Every_image_validator_refuses_the_same_food_claims(string claim)
    {
        Assert.False(Concept(claim).Succeeded, $"the photography concept validator allowed \"{claim}\"");
        Assert.False(Prompt(claim).Succeeded, $"the image prompt validator allowed \"{claim}\"");
        Assert.False(Reference(claim).Succeeded, $"the reference image validator allowed \"{claim}\"");
    }

    /// <summary>
    /// IMG-004 alone accepts mood language the other two refuse.
    /// </summary>
    /// <remarks>
    /// "Wholesome" claims nothing about the food in a reading of a photograph — it is an answer to "how does
    /// this picture feel", which is an aspect this document has and neither sibling does. Refusing it there
    /// would cost a paid retry to prevent a word that says nothing.
    /// </remarks>
    [Theory]
    [InlineData("wholesome and unhurried")]
    [InlineData("an authentic, lived-in kitchen")]
    public void The_reference_reader_allows_mood_words_its_siblings_refuse(string mood)
    {
        Assert.False(Prompt(mood).Succeeded);
        Assert.True(Reference(mood).Succeeded, Reference(mood).Failure?.Message);
    }

    /// <summary>
    /// IMG-004 alone accepts the food words that are also render settings.
    /// </summary>
    /// <remarks>
    /// A poppy seed and stone steps are subject and composition vocabulary in a reading of a photograph. The
    /// siblings compose prompts and never describe a reference, so a bare "seed" there is far likelier to be
    /// a provider flag than a bun.
    /// </remarks>
    [Theory]
    [InlineData("a poppy seed crust")]
    [InlineData("stone steps behind the table")]
    public void The_reference_reader_allows_food_words_that_are_also_settings(string text)
    {
        Assert.False(Prompt(text).Succeeded);
        Assert.True(Reference(text).Succeeded, Reference(text).Failure?.Message);
    }

    /// <summary>And a real directive is still refused by all three.</summary>
    [Theory]
    [InlineData("rendered with seed 4412")]
    [InlineData("30 steps at 7 guidance scale")]
    [InlineData("in the style of Midjourney v6")]
    public void Every_image_validator_refuses_a_real_render_directive(string directive)
    {
        Assert.False(Prompt(directive).Succeeded, $"the image prompt validator allowed \"{directive}\"");
        Assert.False(Reference(directive).Succeeded, $"the reference image validator allowed \"{directive}\"");
    }

    private const string Lede =
        "Overhead square-crop photograph of a round loaf on a pale oak board over undyed linen, ";

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument> Concept(string text) =>
        AiPhotographyConceptOutputValidator.Validate(
            $$"""
            {
              "schemaVersion": "v1",
              "concepts": [
                {
                  "conceptId": "11111111-1111-1111-1111-111111111111",
                  "label": "A quiet table",
                  "mood": {{System.Text.Json.JsonSerializer.Serialize(Lede + text)}},
                  "palette": "Warm neutrals, undyed linen, pale oak",
                  "rationale": "It suits the channel and the subject.",
                  "channelFit": "Square crop reads well in a feed.",
                  "shots": [
                    {
                      "kind": "Hero",
                      "framing": "Overhead square crop",
                      "lighting": "Soft daylight from the left",
                      "surface": "Pale oak board",
                      "styling": "Undyed linen, a linen napkin",
                      "props": ["linen napkin"]
                    }
                  ]
                }
              ],
              "warnings": []
            }
            """,
            "v1");

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument> Prompt(string text) =>
        AiImagePromptOutputValidator.Validate(
            $$"""
            {
              "schemaVersion": "v1",
              "prompt": {{System.Text.Json.JsonSerializer.Serialize(Lede + text)}},
              "avoid": [],
              "warnings": []
            }
            """,
            "v1");

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument> Reference(string text) =>
        AiReferenceImageOutputValidator.Validate(
            $$"""
            {
              "schemaVersion": "v1",
              "observations": [
                {
                  "aspect": "Mood",
                  "text": {{System.Text.Json.JsonSerializer.Serialize(text)}},
                  "confidence": "Clear"
                },
                {
                  "aspect": "Lighting",
                  "text": "Soft directional daylight from the left.",
                  "confidence": "Clear"
                }
              ],
              "prompt": {{System.Text.Json.JsonSerializer.Serialize(Lede + "daylight from the left.")}},
              "avoid": [],
              "warnings": []
            }
            """,
            "v1");
}
