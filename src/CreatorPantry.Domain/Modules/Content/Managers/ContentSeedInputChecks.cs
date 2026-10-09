namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The shape rules for a seed request, returned as <c>(Field, Message)</c> pairs so the validator and Business
/// cannot drift apart.
/// </summary>
/// <remarks>
/// Shape only. Whether a pinned key names an entry that exists and is still offered depends on the catalogues, so
/// it is a domain question and Business answers it.
/// </remarks>
internal static class ContentSeedInputChecks
{
    /// <summary>Trimmed, and null when nothing is left. The form every field is read in.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static IEnumerable<(string, string)> Query(ContentSeedQueryViewModel model)
    {
        if (Normalize(model.Token) is { } token && !ContentSeedPolicy.IsToken(token))
        {
            yield return (nameof(model.Token),
                $"A seed token is up to {ContentSeedPolicy.TokenMaxLength} letters, digits, hyphens or underscores.");
        }

        if (model.Day is { } day && !Enum.IsDefined(day))
        {
            yield return (nameof(model.Day), "Name a day of the week, such as Monday.");
        }

        if (model.RecipeId is null && model.RecipeVersionId is not null)
        {
            yield return (nameof(model.RecipeVersionId), "A recipe version needs the recipe it belongs to.");
        }

        if (Normalize(model.Subject) is { } subject)
        {
            if (subject.Length > ContentSeedPolicy.SubjectMaxLength)
            {
                yield return (nameof(model.Subject),
                    $"A subject is at most {ContentSeedPolicy.SubjectMaxLength} characters.");
            }

            // Refused rather than one quietly winning: the caller decided which answer this idea is about, and
            // a server that picked for them would answer a question nobody asked.
            if (model.RecipeId is not null)
            {
                yield return (nameof(model.Subject),
                    "An idea is about a linked recipe or a subject you name, not both.");
            }
        }

        // The pinned facet keys are checked for length only. Their real test is whether a catalogue has them,
        // which Business asks, and a key that is merely unknown gets the same answer as one shaped wrongly — so
        // a second shape rule here would only ever duplicate it.
        foreach (var (field, value) in new[]
        {
            (nameof(model.Cuisine), model.Cuisine),
            (nameof(model.DishType), model.DishType),
            (nameof(model.Method), model.Method),
            (nameof(model.PhotographyStyle), model.PhotographyStyle),
            (nameof(model.Channel), model.Channel),
            (nameof(model.Occasion), model.Occasion),
        })
        {
            if (Normalize(value) is { Length: > ContentSeedPolicy.KeyMaxLength })
            {
                yield return (field, $"A key is at most {ContentSeedPolicy.KeyMaxLength} characters.");
            }
        }
    }
}
