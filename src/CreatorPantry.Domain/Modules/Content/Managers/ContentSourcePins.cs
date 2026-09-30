namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The exact sources one revision was written against, or the current ones to compare them with.
/// </summary>
/// <remarks>
/// A missing brand profile or style guide is <c>null</c>, which compares equal only to another <c>null</c>:
/// gaining or losing one is a change, and content written without a voice is not "current" against a guide
/// that has since been created. The template is compared by body checksum, not version string alone.
/// </remarks>
public sealed record ContentSourcePins(
    Guid RecipeVersionId,
    Guid? BrandProfileRevisionId,
    Guid? BrandStyleGuideVersionId,
    string? PromptTemplateId,
    string? PromptTemplateVersion,
    string? PromptTemplateBodyChecksum)
{
    /// <summary>What moved between these pins and <paramref name="current"/>; <see cref="ContentStaleReasons.None"/> when nothing did.</summary>
    public ContentStaleReasons StaleAgainst(ContentSourcePins current)
    {
        var reasons = ContentStaleReasons.None;

        if (RecipeVersionId != current.RecipeVersionId)
        {
            reasons |= ContentStaleReasons.RecipeChanged;
        }

        if (BrandProfileRevisionId != current.BrandProfileRevisionId)
        {
            reasons |= ContentStaleReasons.BrandChanged;
        }

        if (BrandStyleGuideVersionId != current.BrandStyleGuideVersionId)
        {
            reasons |= ContentStaleReasons.VoiceChanged;
        }

        if (PromptTemplateId != current.PromptTemplateId
            || PromptTemplateVersion != current.PromptTemplateVersion
            || PromptTemplateBodyChecksum != current.PromptTemplateBodyChecksum)
        {
            reasons |= ContentStaleReasons.TemplateChanged;
        }

        return reasons;
    }
}
