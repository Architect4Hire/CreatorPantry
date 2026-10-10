namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The shape rules for a post revision, in one place so the write seam and whatever validator AF.6.4 puts in
/// front of it cannot disagree. Each returns the field at fault and what is wrong with it.
/// </summary>
public static class SocialPackageInputChecks
{
    public static IEnumerable<(string Field, string Error)> Generated(SocialGeneratedRevisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        foreach (var failure in Common(input.ChannelKey, input.Body, input.Limit))
        {
            yield return failure;
        }

        if (input.AiProposalId == Guid.Empty)
        {
            yield return ("aiProposalId", "A generated post names the AI proposal that produced it.");
        }

        if (Blank(input.PromptTemplateId) || input.PromptTemplateId.Length > ContentPolicy.TemplateIdMaxLength
            || Blank(input.PromptTemplateVersion) || input.PromptTemplateVersion.Length > ContentPolicy.TemplateVersionMaxLength
            || Blank(input.PromptTemplateBodyChecksum) || input.PromptTemplateBodyChecksum.Length > ContentPolicy.ChecksumMaxLength)
        {
            yield return ("promptTemplateId", "A generated post names its template's id, version and body checksum.");
        }

        // Half a pin names a recipe with nothing to compare, or a version of no stated recipe.
        if (input.RecipeId is null != input.RecipeVersionId is null)
        {
            yield return ("recipeVersionId", "A recipe pin is a recipe and one of its versions, or neither.");
        }

        if (input.CreativeContextVersion is { Length: > ContentPolicy.ContextVersionMaxLength })
        {
            yield return ("creativeContextVersion", "That is not a creative context version.");
        }

        if (input.ContextPackageChecksum is { Length: > ContentPolicy.ChecksumMaxLength })
        {
            yield return ("contextPackageChecksum", "That is not a context package checksum.");
        }
    }

    public static IEnumerable<(string Field, string Error)> Edited(SocialEditedRevisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return Common(input.ChannelKey, input.Body, input.Limit);
    }

    private static IEnumerable<(string Field, string Error)> Common(string channelKey, string body, SocialLimitResult? limit)
    {
        if (Blank(channelKey) || channelKey.Trim().Length > ContentPolicy.ChannelKeyMaxLength)
        {
            yield return ("channelKey", "Name the channel this post is for.");
        }

        if (Blank(body))
        {
            yield return ("body", "A post needs some words.");
        }
        else if (body.Length > ContentPolicy.SocialBodyMaxLength)
        {
            yield return ("body", $"A post is at most {ContentPolicy.SocialBodyMaxLength} characters.");
        }

        if (limit is null)
        {
            yield break;
        }

        // A supplied result is a measurement. One that disagrees with its own numbers would store a verdict
        // nobody could explain, and the table's check would refuse it less helpfully than this does.
        var consistent = limit.Status switch
        {
            SocialLimitStatus.Within => limit.CharacterLimit is not { } within || limit.CharacterCount <= within,
            SocialLimitStatus.Over => limit.CharacterLimit is { } over && limit.CharacterCount > over,
            _ => false,
        };

        if (!consistent
            || limit.CharacterCount < 0
            || limit.CharacterLimit is < 1
            || Blank(limit.ChannelProfileVersion)
            || limit.ChannelProfileVersion.Length > ContentPolicy.ChannelProfileVersionMaxLength)
        {
            yield return ("limit", "The limit result does not agree with its own count, limit and profile version.");
        }
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
