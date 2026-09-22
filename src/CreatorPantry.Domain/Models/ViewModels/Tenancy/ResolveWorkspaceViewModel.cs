namespace CreatorPantry.Domain.Models.ViewModels.Tenancy;

/// <summary>The route slug identifying a workspace, before it is resolved to an id and membership.</summary>
public sealed record ResolveWorkspaceViewModel(string WorkspaceSlug);
