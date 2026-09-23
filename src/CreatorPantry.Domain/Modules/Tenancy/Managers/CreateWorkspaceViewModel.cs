namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>Creates a workspace. The address (slug) is derived from <see cref="Name"/>, not client-supplied.</summary>
public sealed record CreateWorkspaceViewModel(string Name);
