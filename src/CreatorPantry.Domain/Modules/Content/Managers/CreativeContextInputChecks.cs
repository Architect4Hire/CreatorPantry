namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The shape rules for a creative context's input, in one place so the validators at the edge and the
/// backstop in Business cannot disagree.
/// </summary>
/// <remarks>
/// Shape only: lengths, which fields a kind carries, whether a key could be a key. Whether a channel exists,
/// a theme is this workspace's or a recipe is there to be named are questions for other modules' facades, and
/// Business asks them.
/// </remarks>
internal static class CreativeContextInputChecks
{
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>The channel keys as they will be stored: trimmed, in the order given.</summary>
    public static IReadOnlyList<string> NormalizeChannels(IReadOnlyList<string?>? keys) =>
        keys is null ? [] : [.. keys.Select(Normalize).Where(key => key is not null).Select(key => key!)];

    public static IEnumerable<(string, string)> Create(CreateCreativeContextViewModel model)
    {
        foreach (var failure in Words(model.WorkingTitle, model.PictureBrief))
        {
            yield return failure;
        }

        foreach (var failure in Channels(model.ChannelKeys))
        {
            yield return failure;
        }

        foreach (var failure in Day(model.Day))
        {
            yield return failure;
        }

        foreach (var failure in Theme(model.WeeklyThemeKey))
        {
            yield return failure;
        }

        if (model.From is { } from)
        {
            foreach (var failure in Reference(from, "from"))
            {
                yield return failure;
            }
        }
    }

    public static IEnumerable<(string, string)> Patch(PatchCreativeContextViewModel model)
    {
        foreach (var failure in Words(
            model.WorkingTitle.Or(null),
            model.PictureBrief.Or(null)))
        {
            yield return failure;
        }

        if (Normalize(model.WorkingBrief.Or(null)) is { Length: > CreativeContextPolicy.WorkingBriefMaxLength })
        {
            yield return ("workingBrief", $"The brief is at most {CreativeContextPolicy.WorkingBriefMaxLength} characters.");
        }

        if (model.BriefSource.Or(null) is { } source && !Enum.IsDefined(source))
        {
            yield return ("briefSource", "Say what the brief is from: Description, Idea or Combined.");
        }

        if (model.ChannelKeys.IsSubmitted)
        {
            foreach (var failure in Channels(model.ChannelKeys.Value))
            {
                yield return failure;
            }
        }

        foreach (var failure in Day(model.Day.Or(null)))
        {
            yield return failure;
        }

        foreach (var failure in Theme(model.WeeklyThemeKey.Or(null)))
        {
            yield return failure;
        }

        // Sent, and sent as null: there is no third state to clear to.
        if (model.Archived.IsSubmitted && model.Archived.Value is null)
        {
            yield return ("archived", "Send true to archive or false to restore.");
        }
    }

    public static IEnumerable<(string, string)> Add(AddCreativeContextReferenceViewModel model) =>
        model.Reference is { } reference
            ? Reference(reference, "reference")
            : [("reference", "Say what to add.")];

    /// <summary>
    /// A reference carries its kind's ids and no others — the rule the table's own check constraint holds,
    /// answered here with the field that is wrong rather than as a storage error.
    /// </summary>
    public static IEnumerable<(string, string)> Reference(CreativeContextReferenceInputViewModel reference, string prefix)
    {
        if (reference.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            yield return ($"{prefix}.kind", "Say what kind of source this is, such as Recipe or DamAsset.");
            yield break;
        }

        if (kind is CreativeContextReferenceKind.SocialPackage)
        {
            // No table to resolve one against until post packages exist, and an id nothing can check is an id
            // nothing should store.
            yield return ($"{prefix}.kind", "A post package cannot be added to a creative context yet.");
            yield break;
        }

        var fields = new (string Name, bool Present, CreativeContextReferenceKind Owner, bool Required)[]
        {
            ("recipeId", reference.RecipeId is not null, CreativeContextReferenceKind.Recipe, true),
            ("recipeVersionId", reference.RecipeVersionId is not null, CreativeContextReferenceKind.Recipe, false),
            ("conceptRequestId", reference.ConceptRequestId is not null, CreativeContextReferenceKind.RecipeConcept, true),
            ("conceptId", reference.ConceptId is not null, CreativeContextReferenceKind.RecipeConcept, true),
            ("mediaAssetId", reference.MediaAssetId is not null, CreativeContextReferenceKind.DamAsset, true),
            ("mediaAssetVersionNumber", reference.MediaAssetVersionNumber is not null, CreativeContextReferenceKind.DamAsset, false),
            ("generatedImageId", reference.GeneratedImageId is not null, CreativeContextReferenceKind.GeneratedImage, true),
            ("promptRecordId", reference.PromptRecordId is not null, CreativeContextReferenceKind.PromptRecord, true),
        };

        foreach (var (name, present, owner, required) in fields)
        {
            if (owner == kind && required && !present)
            {
                yield return ($"{prefix}.{name}", $"A {kind} reference needs this.");
            }
            else if (owner != kind && present)
            {
                yield return ($"{prefix}.{name}", $"A {kind} reference does not carry this.");
            }
        }

        if (kind is CreativeContextReferenceKind.DamAsset && reference.MediaAssetVersionNumber is < 1)
        {
            yield return ($"{prefix}.mediaAssetVersionNumber", "Version numbers start at 1.");
        }
    }

    private static IEnumerable<(string, string)> Words(string? workingTitle, string? pictureBrief)
    {
        if (Normalize(workingTitle) is { Length: > CreativeContextPolicy.WorkingTitleMaxLength })
        {
            yield return ("workingTitle", $"A working title is at most {CreativeContextPolicy.WorkingTitleMaxLength} characters.");
        }

        if (Normalize(pictureBrief) is { Length: > CreativeContextPolicy.PictureBriefMaxLength })
        {
            yield return ("pictureBrief", $"The picture description is at most {CreativeContextPolicy.PictureBriefMaxLength} characters.");
        }
    }

    private static IEnumerable<(string, string)> Channels(IReadOnlyList<string?>? keys)
    {
        if (keys is null)
        {
            yield break;
        }

        if (keys.Count > CreativeContextPolicy.MaxChannels)
        {
            yield return ("channelKeys", $"Choose at most {CreativeContextPolicy.MaxChannels} channels.");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < keys.Count; index++)
        {
            var key = Normalize(keys[index]);

            if (key is null || key.Length > ContentPolicy.ChannelKeyMaxLength)
            {
                yield return ($"channelKeys[{index}]", "Name a channel by its key, such as instagram.");
            }
            else if (!seen.Add(key))
            {
                yield return ($"channelKeys[{index}]", "Each channel can be chosen once.");
            }
        }
    }

    private static IEnumerable<(string, string)> Day(DayOfWeek? day)
    {
        if (day is { } value && !Enum.IsDefined(value))
        {
            yield return ("day", "Name a day of the week, such as Monday.");
        }
    }

    private static IEnumerable<(string, string)> Theme(string? weeklyThemeKey)
    {
        if (Normalize(weeklyThemeKey) is { } key && !WeeklyThemeInputChecks.IsKey(key))
        {
            yield return ("weeklyThemeKey", "Use one of this workspace's theme keys, such as meat-free-monday.");
        }
    }
}
