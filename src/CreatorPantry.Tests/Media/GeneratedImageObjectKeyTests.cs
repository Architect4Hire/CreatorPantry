using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The staging key grammar: deterministic, built from identifiers alone, and unforgiving on the way back in.
/// </summary>
public sealed class GeneratedImageObjectKeyTests
{
    private static readonly Guid Workspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void The_same_variant_always_gets_the_same_key()
    {
        // This is what makes a replay find its own orphan instead of leaving a second one beside it.
        Assert.Equal(
            GeneratedImageObjectKey.For(Workspace, Operation, 2),
            GeneratedImageObjectKey.For(Workspace, Operation, 2));
    }

    [Fact]
    public void A_key_is_the_workspace_then_the_operation_then_the_variant()
    {
        Assert.Equal(
            $"workspaces/{Workspace:N}/generated-images/{Operation:N}/2",
            GeneratedImageObjectKey.For(Workspace, Operation, 2));
    }

    [Fact]
    public void A_key_this_type_wrote_parses_back_into_its_parts()
    {
        Assert.True(GeneratedImageObjectKey.TryParse(
            GeneratedImageObjectKey.For(Workspace, Operation, 3), out var parts));

        Assert.Equal(Workspace, parts.WorkspaceId);
        Assert.Equal(Operation, parts.OperationId);
        Assert.Equal(3, parts.VariantIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("workspaces/../generated-images/x/0")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/generated-images/../../etc/passwd")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/generated-images/11111111222233334444555555555555/0/..")]
    [InlineData("https://example.invalid/image.png")]
    [InlineData("brand-sources/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0")]
    [InlineData("workspaces/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/generated-images/11111111222233334444555555555555/0")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/generated-images/11111111222233334444555555555555/0/extra")]
    public void A_key_this_type_would_not_have_written_is_refused(string? candidate)
    {
        Assert.False(GeneratedImageObjectKey.TryParse(candidate, out _));
    }

    /// <summary>A separator the grammar does not use is not a separator.</summary>
    [Fact]
    public void A_key_written_with_backslashes_is_refused()
    {
        Assert.False(GeneratedImageObjectKey.TryParse(
            GeneratedImageObjectKey.For(Workspace, Operation, 0).Replace('/', '\\'), out _));
    }

    [Fact]
    public void A_trailing_newline_does_not_sneak_a_key_past_the_pattern()
    {
        Assert.False(GeneratedImageObjectKey.TryParse(
            GeneratedImageObjectKey.For(Workspace, Operation, 0) + "\n", out _));
    }

    [Fact]
    public void A_variant_past_the_policy_cap_is_neither_written_nor_read_back()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GeneratedImageObjectKey.For(Workspace, Operation, MediaPolicy.MaxVariantsPerOperation));

        // And a key that got one past the grammar's single digit is still refused on the way in.
        Assert.False(GeneratedImageObjectKey.TryParse(
            $"workspaces/{Workspace:N}/generated-images/{Operation:N}/{MediaPolicy.MaxVariantsPerOperation}",
            out _));
    }

    [Fact]
    public void A_key_needs_real_identifiers()
    {
        Assert.Throws<ArgumentException>(() => GeneratedImageObjectKey.For(Guid.Empty, Operation, 0));
        Assert.Throws<ArgumentException>(() => GeneratedImageObjectKey.For(Workspace, Guid.Empty, 0));
    }

    [Fact]
    public void A_key_fits_the_column_that_stores_it()
    {
        Assert.True(
            GeneratedImageObjectKey.For(Workspace, Operation, 0).Length <= MediaPolicy.ObjectKeyMaxLength);
    }

    [Fact]
    public void Every_failure_category_says_whether_it_is_worth_retrying()
    {
        // Retryable is a subset of All, so a category added without a decision about retrying it shows up
        // here rather than silently defaulting to "no".
        Assert.Subset(GeneratedImageFailureCategory.All.ToHashSet(StringComparer.Ordinal),
            GeneratedImageFailureCategory.Retryable.ToHashSet(StringComparer.Ordinal));
    }
}
