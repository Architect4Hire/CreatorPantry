namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>One value a template's body expects, declared in its manifest.</summary>
/// <param name="Name">camelCase; matches the <c>{{name}}</c> placeholder in the body exactly.</param>
/// <param name="Required">
/// Whether the caller must supply it. An optional input still has to appear in the body — the manifest
/// declares what the template reads, not what a caller happens to have.
/// </param>
/// <param name="Description">
/// What the value is and where it comes from, for whoever reads the template next. Not used at runtime.
/// </param>
public sealed record PromptTemplateInput(string Name, bool Required, string? Description);
