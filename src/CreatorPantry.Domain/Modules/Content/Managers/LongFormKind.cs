namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a long-form draft is (baseline B-32). Stored as its number; append, never renumber.
/// </summary>
public enum LongFormKind
{
    /// <summary>A full blog article.</summary>
    BlogArticle = 0,

    /// <summary>A full newsletter issue, which may cover more than one source (B-35).</summary>
    NewsletterIssue = 1,
}
