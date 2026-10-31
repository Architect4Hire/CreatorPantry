using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The fit rule (AP.3.2) as arithmetic: what is sent, how long it is, and what is left for the prompt once the
/// avoid list is set aside whole.
/// </summary>
public sealed class GeneratedImagePromptFitTests
{
    [Fact]
    public void What_is_sent_is_the_prompt_then_the_avoid_list_under_its_heading()
    {
        Assert.Equal("soup\n\nAvoid the following: flash", GeneratedImagePromptComposition.Compose("soup", "flash"));
        Assert.Equal("soup", GeneratedImagePromptComposition.Compose("soup", null));
        Assert.Equal("soup", GeneratedImagePromptComposition.Compose("soup", "   "));
    }

    [Theory]
    [InlineData(100, null, 100, GeneratedImagePromptFitOutcome.Fits)]
    [InlineData(101, null, 100, GeneratedImagePromptFitOutcome.NeedsShortening)]
    [InlineData(10, 10, 100, GeneratedImagePromptFitOutcome.Fits)]
    [InlineData(100, 10, 100, GeneratedImagePromptFitOutcome.NeedsShortening)]
    public void A_prompt_fits_exactly_when_everything_sent_is_within_the_limit(
        int promptLength, int? avoidLength, int limit, GeneratedImagePromptFitOutcome expected)
    {
        var prompt = new string('p', promptLength);
        var avoid = avoidLength is { } length ? new string('a', length) : null;

        var fit = GeneratedImagePromptComposition.Measure(prompt, avoid, limit);

        Assert.Equal(expected, fit.Outcome);
        Assert.Equal(GeneratedImagePromptComposition.Compose(prompt, avoid).Length, fit.Length);
        Assert.Equal(limit, fit.Limit);
    }

    /// <summary>The budget is the limit less the heading and the avoid list — all of it, never a shortened one.</summary>
    [Fact]
    public void The_budget_is_what_the_avoid_list_and_its_heading_leave()
    {
        var avoid = new string('a', 1000);

        var fit = GeneratedImagePromptComposition.Measure(new string('p', 7000), avoid, 7500);

        Assert.Equal(GeneratedImagePromptFitOutcome.NeedsShortening, fit.Outcome);
        Assert.Equal(7500 - GeneratedImagePromptComposition.AvoidHeading.Length - 1000, fit.PromptBudget);

        // A prompt of exactly that budget, with that avoid list, fits.
        Assert.True(GeneratedImagePromptComposition.Measure(new string('p', fit.PromptBudget), avoid, 7500).Fits);
    }

    [Fact]
    public void An_avoid_list_that_leaves_less_than_the_shortest_prompt_is_the_avoid_lists_problem()
    {
        var limit = 200;
        var heading = GeneratedImagePromptComposition.AvoidHeading.Length;
        var atTheEdge = new string('a', limit - heading - MediaPolicy.ShortestPromptBudget);
        var overTheEdge = atTheEdge + "a";

        Assert.Equal(
            GeneratedImagePromptFitOutcome.NeedsShortening,
            GeneratedImagePromptComposition.Measure(new string('p', 500), atTheEdge, limit).Outcome);
        Assert.Equal(
            GeneratedImagePromptFitOutcome.AvoidTooLong,
            GeneratedImagePromptComposition.Measure(new string('p', 500), overTheEdge, limit).Outcome);
    }

    /// <summary>
    /// Media refuses an avoid list for the room it leaves; the shorter-prompt task refuses an answer under its own
    /// floor. If the two numbers drifted, a request could be offered a budget no answer could meet.
    /// </summary>
    [Fact]
    public void The_shortest_budget_is_the_shortest_prompt_the_task_accepts()
    {
        Assert.Equal(AiPolicy.ImagePromptMinLength, MediaPolicy.ShortestPromptBudget);
    }

    /// <summary>
    /// The prompt is never cut, so the composed IMG-002 answer at its largest still fits the shortest limit a
    /// shipped profile has — the case AP.3.2 says only a creator's own long text reaches.
    /// </summary>
    [Fact]
    public void The_largest_composed_prompt_fits_the_shortest_shipped_limit()
    {
        var avoid = string.Join("; ", Enumerable.Repeat(new string('a', AiPolicy.ImagePromptAvoidMaxLength), AiPolicy.MaxImagePromptAvoidCount));

        var fit = GeneratedImagePromptComposition.Measure(new string('p', AiPolicy.ImagePromptMaxLength), avoid, 7500);

        Assert.True(fit.Fits, $"{fit.Length} characters");
    }
}
