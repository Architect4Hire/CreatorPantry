using CreatorPantry.Domain.Managers.Prompts;

namespace CreatorPantry.Tests.Prompts;

public sealed class PromptTemplateVersionTests
{
    [Fact]
    public void Parses_three_parts()
    {
        Assert.Equal(new PromptTemplateVersion(1, 12, 3), PromptTemplateVersion.Parse("1.12.3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("+1.2.3")]
    [InlineData("1. 2.3")]
    public void Refuses_anything_that_is_not_three_parts(string? text)
    {
        Assert.False(PromptTemplateVersion.TryParse(text, out _));
    }

    /// <summary>
    /// Round-tripping is what rejects these. Left alone they would parse to the same value as their canonical
    /// form and then disagree with the file name they were read from.
    /// </summary>
    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    public void Refuses_a_leading_zero_rather_than_silently_canonicalizing(string text)
    {
        Assert.False(PromptTemplateVersion.TryParse(text, out _));
    }

    [Fact]
    public void Orders_numerically_not_lexically()
    {
        var ordered = new[]
        {
            PromptTemplateVersion.Parse("1.0.0"),
            PromptTemplateVersion.Parse("1.0.9"),
            PromptTemplateVersion.Parse("1.10.0"),
            PromptTemplateVersion.Parse("2.0.0"),
        };

        var shuffled = new[] { ordered[2], ordered[0], ordered[3], ordered[1] };

        Assert.Equal(ordered, shuffled.Order().ToArray());
    }

    [Fact]
    public void Compares_with_operators()
    {
        var lower = PromptTemplateVersion.Parse("1.9.9");
        var higher = PromptTemplateVersion.Parse("1.10.0");

        Assert.True(lower < higher);
        Assert.True(higher > lower);
        Assert.True(lower <= PromptTemplateVersion.Parse("1.9.9"));
        Assert.True(higher >= PromptTemplateVersion.Parse("1.10.0"));
    }

    [Fact]
    public void Renders_as_major_minor_patch()
    {
        Assert.Equal("2.0.14", PromptTemplateVersion.Parse("2.0.14").ToString());
    }
}
