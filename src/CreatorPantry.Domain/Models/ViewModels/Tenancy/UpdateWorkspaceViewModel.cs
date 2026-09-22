namespace CreatorPantry.Domain.Models.ViewModels.Tenancy;

/// <summary>Renames the resolved workspace. The slug is immutable once created.</summary>
public sealed record UpdateWorkspaceViewModel(string Name);
