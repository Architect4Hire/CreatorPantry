using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The promises IMG-001's document makes about its own shape, and the three rules its validator carries that
/// no fixture can state structurally.
/// </summary>
/// <remarks>
/// The validator's behaviour is demonstrated by the <c>image.photography-concept.*</c> evaluation fixtures
/// against the real validator; what is here is what a fixture cannot say — a field that must never exist, a
/// vocabulary that must not drift from another module's, and a stored concept that must have no path to a
/// recipe edit.
/// </remarks>
public sealed class AiPhotographyConceptOutputTests
{
    /// <summary>
    /// IMG-001 composes no prompt, and the document is what enforces it rather than a rule that could be
    /// relaxed.
    /// </summary>
    /// <remarks>
    /// No field named like a finished prompt, anywhere in the document or its nested types. Strict
    /// deserialization then refuses one a model invents, which is the behaviour the
    /// <c>a-composed-prompt-field-is-rejected</c> fixture pins — this is the structural half of the same
    /// guarantee, and the half that survives someone adding a property for convenience.
    /// </remarks>
    [Fact]
    public void No_type_in_the_document_has_a_field_for_a_finished_prompt()
    {
        Type[] types =
        [
            typeof(AiPhotographyConceptOutputDocument),
            typeof(AiPhotographyConcept),
            typeof(AiPhotographyShot),
        ];

        var offenders = types
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .Where(name => new[] { "Prompt", "Instruction", "Command", "Render" }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Nor anything that would carry a recipe fact, a URL or an owner.
    /// </summary>
    [Fact]
    public void No_type_in_the_document_has_a_field_for_a_quantity_a_url_or_an_owner()
    {
        Type[] types =
        [
            typeof(AiPhotographyConceptOutputDocument),
            typeof(AiPhotographyConcept),
            typeof(AiPhotographyShot),
        ];

        var offenders = types
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .Where(name => new[]
                {
                    "Quantity", "Temperature", "Minutes", "Yield", "Serving",
                    "Url", "Uri", "Path", "Workspace", "Membership",
                }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Every shot role shares a name with a <c>PromptImageKind</c>, so 12.4a's mapping stays a lookup.
    /// </summary>
    /// <remarks>
    /// The AI module deliberately owns its own enum — the exported schema must contain exactly the values an
    /// answer may use, and <c>PromptImageKind</c> carries three that are output formats rather than shoot
    /// roles. The names are what keeps that a deliberate subset rather than two vocabularies drifting: when
    /// IMG-002 writes an approved shot's prompt to the library it needs an <c>ImageKind</c> for the row, and
    /// this is what makes that mapping unambiguous.
    /// </remarks>
    [Fact]
    public void Every_shot_role_names_a_prompt_image_kind()
    {
        var kinds = Enum.GetNames<PromptImageKind>().ToHashSet(StringComparer.Ordinal);

        var orphans = Enum.GetNames<AiPhotographyShotKind>()
            .Where(role => !kinds.Contains(role))
            .ToList();

        Assert.Empty(orphans);
    }

    /// <summary>
    /// The shot roles are a strict subset: the output formats are left out on purpose.
    /// </summary>
    [Fact]
    public void The_shot_roles_leave_out_the_output_formats_and_the_shrug()
    {
        var roles = Enum.GetNames<AiPhotographyShotKind>().ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(nameof(PromptImageKind.SocialTile), roles);
        Assert.DoesNotContain(nameof(PromptImageKind.PinGraphic), roles);
        Assert.DoesNotContain(nameof(PromptImageKind.Other), roles);
    }

    /// <summary>
    /// A stored photography concept has no path to a recipe edit, which is IMG-001's "no recipe mutation".
    /// </summary>
    /// <remarks>
    /// The same guarantee <c>RecipeReviewFinding</c> makes, and it matters more here: a concept may be planned
    /// against a pinned recipe version, so the one thing a photograph must never do is change the dish it is a
    /// photograph of. Absence from both tables is the mechanism; this is what keeps it absent.
    /// </remarks>
    [Fact]
    public void A_photography_concept_can_never_become_a_recipe_change()
    {
        Assert.DoesNotContain(AiChangeTargetKind.PhotographyConcept, AiChangeApplicability.Targets);
        Assert.Empty(AiChangeApplicability.For(AiChangeTargetKind.PhotographyConcept));
        Assert.Null(AiChangeTargetPolicy.For(AiChangeTargetKind.PhotographyConcept));

        // Every change kind, so no single verb slips through the table's omission.
        foreach (var kind in Enum.GetValues<AiChangeKind>())
        {
            Assert.False(AiChangeApplicability.IsApplicable(kind, AiChangeTargetKind.PhotographyConcept));
        }
    }

    /// <summary>The exported schema names the document's own fields, so a prompt asks for what is validated.</summary>
    [Fact]
    public void The_exported_schema_describes_the_concept_and_its_shots()
    {
        var schema = AiPhotographyConceptOutputSchema.Json;

        foreach (var expected in new[] { "concepts", "shots", "framing", "lighting", "surface", "styling", "Hero" })
        {
            Assert.Contains(expected, schema, StringComparison.Ordinal);
        }

        // And it does not offer a role the validator would refuse.
        Assert.DoesNotContain("SocialTile", schema, StringComparison.Ordinal);
    }
}
