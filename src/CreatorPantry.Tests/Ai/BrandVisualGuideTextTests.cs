using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Ai;

public sealed class BrandVisualGuideTextTests
{
    [Theory]
    [InlineData("photography-concept", AiTaskType.PhotographyConcept)]
    [InlineData("image-prompt", AiTaskType.ImagePrompt)]
    [InlineData(" image-prompt ", AiTaskType.ImagePrompt)]
    public void Only_the_image_tasks_parse(string value, AiTaskType expected) =>
        Assert.Equal(expected, BrandVisualGuideTasks.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ImagePrompt")]
    [InlineData("editorial-package")]
    [InlineData("seo-package")]
    public void Anything_else_is_not_an_image_task(string? value) =>
        Assert.Null(BrandVisualGuideTasks.Parse(value));

    [Fact]
    public void Every_task_it_answers_for_is_grounded_in_the_brands_look_and_not_its_voice()
    {
        foreach (var task in new[] { BrandVisualGuideTasks.PhotographyConcept, BrandVisualGuideTasks.ImagePrompt })
        {
            var type = BrandVisualGuideTasks.Parse(task)!.Value;

            Assert.True(BrandContextSelection.AppliesTo(type), task);
            Assert.True(BrandContextSelection.IsVisual(type), task);
        }
    }

    [Fact]
    public void The_two_preview_routes_never_overlap_on_a_task()
    {
        foreach (var task in new[] { BrandVisualGuideTasks.PhotographyConcept, BrandVisualGuideTasks.ImagePrompt })
        {
            Assert.Null(BrandWritingGuideTasks.Parse(task));
        }

        foreach (var task in new[] { BrandWritingGuideTasks.EditorialPackage, BrandWritingGuideTasks.SeoPackage })
        {
            Assert.Null(BrandVisualGuideTasks.Parse(task));
        }
    }

    [Theory]
    [InlineData(BrandSourceExtractionState.NotExtracted, "We have not read it yet.")]
    [InlineData(BrandSourceExtractionState.Unsupported, "We cannot read text from this kind of file.")]
    [InlineData(BrandSourceExtractionState.Failed, "We could not read it.")]
    [InlineData(BrandSourceExtractionState.Succeeded, "Its text is not ready to use yet.")]
    public void Every_reason_is_plain_and_ends_with_the_guarantee_about_the_image(
        BrandSourceExtractionState state, string start)
    {
        var reason = BrandVisualReferenceReasons.For(state);

        Assert.StartsWith(start, reason);
        Assert.EndsWith("its image is never sent.", reason);
        Assert.DoesNotMatch("(?i)blob|embedding|chunk|token|provider|extraction|object", reason);
    }

    [Fact]
    public void Every_extraction_state_has_a_reason()
    {
        foreach (var state in Enum.GetValues<BrandSourceExtractionState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(BrandVisualReferenceReasons.For(state)), state.ToString());
        }
    }
}
