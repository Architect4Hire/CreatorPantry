namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Which pinned source moved on. A set, because one change can move several at once.</summary>
[Flags]
public enum ContentStaleReasons
{
    None = 0,
    RecipeChanged = 1,
    BrandChanged = 2,
    VoiceChanged = 4,
    TemplateChanged = 8,
}
