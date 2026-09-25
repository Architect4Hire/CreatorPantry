using System.Reflection;
using CreatorPantry.Domain.Managers.Prompts;

namespace CreatorPantry.Tests.Prompts;

/// <summary>
/// The embedded-resource path end to end, over the fixtures under <c>Prompts/Fixtures/</c>. These are compiled
/// into this assembly exactly as CreatorPantry.Domain compiles real templates, so the loader is exercised
/// through the mechanism it actually uses rather than a string handed to it.
/// </summary>
public sealed class PromptTemplateStoreTests
{
    private const string SoundFixtures = "CreatorPantry.Tests.Prompts.Fixtures.Sound.";
    private const string DuplicatedFixtures = "CreatorPantry.Tests.Prompts.Fixtures.Duplicated.";

    private static Assembly TestAssembly => typeof(PromptTemplateStoreTests).Assembly;

    private static EmbeddedPromptTemplateStore Sound() => EmbeddedPromptTemplateStore.Load(
        name => name.StartsWith(SoundFixtures, StringComparison.Ordinal),
        TestAssembly);

    [Fact]
    public void Loads_every_embedded_template()
    {
        var store = Sound();

        Assert.Equal(3, store.All.Count);
        Assert.Contains(store.All, template => template is { Id: "fixture.angles" });
    }

    [Fact]
    public void Get_by_id_returns_the_highest_version()
    {
        var template = Sound().Get("fixture.headnote");

        Assert.Equal(new PromptTemplateVersion(1, 2, 0), template.Version);
        Assert.Equal("fixture.headnote.v2", template.OutputSchemaVersion);
    }

    /// <summary>
    /// The reason the store keeps more than one version: a stored generation records the version it used, and
    /// that has to resolve back to the exact body long after the template moved on.
    /// </summary>
    [Fact]
    public void Get_by_exact_version_returns_the_superseded_body()
    {
        var template = Sound().Get("fixture.headnote", PromptTemplateVersion.Parse("1.0.0"));

        Assert.Equal("fixture.headnote.v1", template.OutputSchemaVersion);
        Assert.DoesNotContain("servings", template.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_id_fails_by_name()
    {
        var failure = Assert.Throws<PromptTemplateException>(() => Sound().Get("fixture.absent"));

        Assert.Contains("fixture.absent", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no template with that id", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_version_lists_the_ones_that_exist()
    {
        var failure = Assert.Throws<PromptTemplateException>(() =>
            Sound().Get("fixture.headnote", PromptTemplateVersion.Parse("9.0.0")));

        Assert.Contains("1.2.0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_resources_claiming_one_version_fail_the_load()
    {
        var failure = Assert.Throws<PromptTemplateException>(() => EmbeddedPromptTemplateStore.Load(
            name => name.StartsWith(DuplicatedFixtures, StringComparison.Ordinal),
            TestAssembly));

        Assert.Contains("fixture.twice-1.0.0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("declared by two resources", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CreatorPantry.Domain ships no template yet — the capabilities that need them come later. An assembly
    /// with none is a legitimate state, not a load failure.
    /// </summary>
    [Fact]
    public void An_assembly_with_no_templates_loads_empty()
    {
        var store = EmbeddedPromptTemplateStore.Load(typeof(PromptTemplateFile).Assembly);

        Assert.Empty(store.All);
    }

    [Fact]
    public void A_template_carries_the_checksum_of_the_body_that_was_loaded()
    {
        var template = Sound().Get("fixture.angles");

        Assert.Equal(PromptTemplateFile.ComputeChecksum(template.Body), template.BodyChecksum);
    }
}
