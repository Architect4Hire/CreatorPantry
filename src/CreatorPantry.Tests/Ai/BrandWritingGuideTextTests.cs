using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Ai;

public sealed class BrandWritingGuideTextTests
{
    [Fact]
    public void Every_section_key_has_a_creator_facing_label_with_no_enum_spelling_left_in_it()
    {
        foreach (var key in Enum.GetValues<BrandStyleGuideSectionKey>())
        {
            var label = BrandWritingGuideText.Label(key);

            Assert.False(string.IsNullOrWhiteSpace(label), $"{key} has no label.");
            Assert.DoesNotContain(label.Skip(1), char.IsUpper);
        }
    }

    [Theory]
    [InlineData(BrandStyleGuideSectionKey.PointOfView, "Point of view")]
    [InlineData(BrandStyleGuideSectionKey.CallsToAction, "Calls to action")]
    [InlineData(BrandStyleGuideSectionKey.Voice, "Voice")]
    [InlineData(BrandStyleGuideSectionKey.UserNotes, "User notes")]
    public void Labels_read_as_words(BrandStyleGuideSectionKey key, string expected) =>
        Assert.Equal(expected, BrandWritingGuideText.Label(key));

    [Fact]
    public void Short_text_is_kept_with_its_whitespace_collapsed()
    {
        Assert.Equal("Warm and plain.", BrandWritingGuideText.Summarise("  Warm   and\n plain. "));
    }

    [Fact]
    public void Long_text_is_cut_at_a_word_and_marked_never_paraphrased()
    {
        var text = string.Join(' ', Enumerable.Repeat("conversational", 40));

        var summary = BrandWritingGuideText.Summarise(text);

        Assert.EndsWith("…", summary);
        Assert.True(summary.Length <= BrandWritingGuideText.SummaryLimit + 1);
        Assert.True(text.StartsWith(summary[..^1], StringComparison.Ordinal));
        Assert.DoesNotContain("conversationa…", summary);
    }

    [Fact]
    public void A_single_unbroken_word_is_cut_at_the_limit_rather_than_returned_whole()
    {
        var summary = BrandWritingGuideText.Summarise(new string('x', 400));

        Assert.Equal(BrandWritingGuideText.SummaryLimit + 1, summary.Length);
    }

    [Theory]
    [InlineData("editorial-package", AiTaskType.EditorialPackage)]
    [InlineData("seo-package", AiTaskType.SeoPackage)]
    [InlineData(" seo-package ", AiTaskType.SeoPackage)]
    public void Only_the_writing_tasks_parse(string value, AiTaskType expected) =>
        Assert.Equal(expected, BrandWritingGuideTasks.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EditorialPackage")]
    [InlineData("image-prompt")]
    [InlineData("recipe-review")]
    public void Anything_else_is_not_a_task(string? value) =>
        Assert.Null(BrandWritingGuideTasks.Parse(value));

    [Fact]
    public void Every_task_it_answers_for_is_grounded_in_brand_voice()
    {
        foreach (var task in new[] { BrandWritingGuideTasks.EditorialPackage, BrandWritingGuideTasks.SeoPackage })
        {
            Assert.True(BrandContextSelection.AppliesTo(BrandWritingGuideTasks.Parse(task)!.Value), task);
        }
    }
}
