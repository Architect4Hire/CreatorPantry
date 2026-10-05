using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The rules a prompt save must satisfy before anything is read or written, returned as <c>(Field, Message)</c>
/// pairs so the validator and Business cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// These mirror <c>PromptRecordConfiguration</c>'s check constraints deliberately. The constraints are the
/// authority — they hold whatever code path reaches <c>SaveChanges</c> — but a creator who typed a 5,000
/// character prompt deserves a field error naming the field, not a storage exception, so the same rules are
/// stated once here and asked twice.
/// </para>
/// <para>
/// Whether a pinned recipe, version or proposal <em>exists in this workspace</em> is not here: that needs the
/// owning module's facade, so Business asks it. This is everything answerable from the request alone, plus the
/// channel key, whose authority is a code-owned catalogue rather than a table.
/// </para>
/// </remarks>
internal static class PromptRecordInputChecks
{
    /// <summary>Trimmed, and null when nothing is left. The form every text field is stored in.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static IEnumerable<(string, string)> Save(
        SavePromptRecordViewModel model, IContentChannelCatalog channels)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(channels);

        foreach (var failure in Prompt(model))
        {
            yield return failure;
        }

        foreach (var failure in Channel(model, channels))
        {
            yield return failure;
        }

        foreach (var failure in Provenance(model))
        {
            yield return failure;
        }

        foreach (var failure in RecipePins(model))
        {
            yield return failure;
        }
    }

    private static IEnumerable<(string, string)> Prompt(SavePromptRecordViewModel model)
    {
        var text = Normalize(model.Text);
        if (text is null)
        {
            yield return (nameof(model.Text), "Give the prompt some text.");
        }
        else if (text.Length > ContentPolicy.PromptTextMaxLength)
        {
            yield return (nameof(model.Text),
                $"A prompt can be at most {ContentPolicy.PromptTextMaxLength} characters.");
        }

        if (Normalize(model.GeneratedText) is { Length: > ContentPolicy.PromptTextMaxLength })
        {
            yield return (nameof(model.GeneratedText),
                $"A draft can be at most {ContentPolicy.PromptTextMaxLength} characters.");
        }

        if (Normalize(model.Label) is { Length: > ContentPolicy.PromptLabelMaxLength })
        {
            yield return (nameof(model.Label),
                $"A label can be at most {ContentPolicy.PromptLabelMaxLength} characters.");
        }

        if (model.ImageKind is not { } kind)
        {
            yield return (nameof(model.ImageKind), "Say what the image is for, such as Hero.");
        }
        else if (!Enum.IsDefined(kind))
        {
            yield return (nameof(model.ImageKind), "That is not an image kind this library knows.");
        }
    }

    private static IEnumerable<(string, string)> Channel(
        SavePromptRecordViewModel model, IContentChannelCatalog channels)
    {
        var key = Normalize(model.ChannelKey);

        if (key is null)
        {
            yield return (nameof(model.ChannelKey), "Name the channel this image is for, such as instagram.");
            yield break;
        }

        // A retired channel and an unknown one are separate messages, because they have separate remedies: one
        // is a typo, the other is a channel the creator can no longer newly choose. Neither reveals anything
        // private — the catalogue is the same for every workspace.
        var channel = channels.Find(key);
        if (channel is null)
        {
            yield return (nameof(model.ChannelKey), "That is not a channel CreatorPantry writes for.");
        }
        else if (!channel.IsActive)
        {
            yield return (nameof(model.ChannelKey), $"{channel.DisplayName} is retired and cannot be chosen.");
        }
    }

    /// <summary>
    /// Source, proposal, template and draft agree, or none of them do.
    /// </summary>
    /// <remarks>
    /// The three constraints <c>CK_PromptRecords_AiProposal_Source</c>,
    /// <c>CK_PromptRecords_Template_Generated</c> and <c>CK_PromptRecords_GeneratedText_Source</c>, stated as
    /// messages. They are grouped because they are one rule read four ways: a prompt a model wrote names the
    /// proposal, the template that wrote it and the draft it produced; a prompt the creator typed names none of
    /// the three.
    /// </remarks>
    private static IEnumerable<(string, string)> Provenance(SavePromptRecordViewModel model)
    {
        if (model.Source is not { } source)
        {
            yield return (nameof(model.Source), "Say how the prompt came to exist, such as Manual.");

            yield break;
        }

        if (!Enum.IsDefined(source))
        {
            yield return (nameof(model.Source), "That is not a prompt source this library knows.");

            yield break;
        }

        var template = new[]
        {
            (nameof(model.PromptTemplateId), Normalize(model.PromptTemplateId), ContentPolicy.TemplateIdMaxLength),
            (nameof(model.PromptTemplateVersion), Normalize(model.PromptTemplateVersion), ContentPolicy.TemplateVersionMaxLength),
            (nameof(model.PromptTemplateBodyChecksum), Normalize(model.PromptTemplateBodyChecksum), ContentPolicy.ChecksumMaxLength),
        };

        if (source == PromptRecordSource.Manual)
        {
            if (model.AiProposalId is not null)
            {
                yield return (nameof(model.AiProposalId), "A prompt you wrote yourself has no AI proposal.");
            }

            if (Normalize(model.GeneratedText) is not null)
            {
                yield return (nameof(model.GeneratedText), "A prompt you wrote yourself has no model draft.");
            }

            foreach (var (field, _, _) in template.Where(part => part.Item2 is not null))
            {
                yield return (field, "A prompt you wrote yourself has no template.");
            }

            yield break;
        }

        if ((model.AiProposalId ?? Guid.Empty) == Guid.Empty)
        {
            yield return (nameof(model.AiProposalId), "Name the AI proposal this prompt came from.");
        }

        if (Normalize(model.GeneratedText) is null)
        {
            yield return (nameof(model.GeneratedText), "Send the model's draft beside the prompt you are saving.");
        }

        foreach (var (field, value, maxLength) in template)
        {
            if (value is null)
            {
                yield return (field, "A generated prompt names the template that wrote it, with its checksum.");
            }
            else if (value.Length > maxLength)
            {
                yield return (field, $"This can be at most {maxLength} characters.");
            }
        }
    }

    private static IEnumerable<(string, string)> RecipePins(SavePromptRecordViewModel model)
    {
        if (model.RecipeId == Guid.Empty)
        {
            yield return (nameof(model.RecipeId), "Name a recipe, or leave this out.");
        }

        if (model.RecipeVersionId == Guid.Empty)
        {
            yield return (nameof(model.RecipeVersionId), "Name a version, or leave this out.");
        }

        // A version pin needs its recipe: CK_PromptRecords_RecipeVersion_RequiresRecipe, and the composite
        // foreign key then constrains it to a version of that same recipe.
        if (model.RecipeVersionId is not null && model.RecipeId is null)
        {
            yield return (nameof(model.RecipeId), "A version belongs to a recipe, so name the recipe too.");
        }
    }
}
