namespace CreatorPantry.Domain.Models.ViewModels.Tenancy;

/// <summary>Creates a workspace. The address (slug) is derived from <see cref="Name"/>, not client-supplied.</summary>
public sealed record CreateWorkspaceViewModel(string Name);
