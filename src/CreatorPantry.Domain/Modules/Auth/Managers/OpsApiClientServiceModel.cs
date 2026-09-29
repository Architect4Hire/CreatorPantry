namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>
/// An authenticated ops client (baseline B-14). Carries no key material: the credential that proved this
/// identity is not part of the identity.
/// </summary>
/// <param name="ClientId">Recorded as the actor on every platform audit row the request writes.</param>
/// <param name="Name">The automation's name, recorded beside the id so a rotated key stays legible.</param>
/// <param name="Scopes">The granted <see cref="OpsScopes"/> values.</param>
public sealed record OpsApiClientServiceModel(Guid ClientId, string Name, IReadOnlyList<string> Scopes);
