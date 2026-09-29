using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Auth.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Auth.Data;

/// <summary>
/// Provisions the configured ops API clients (baseline B-14), inserting what is missing and rotating a key
/// that no longer matches the stored hash.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Idempotent by construction.</strong> The salt is derived from the key's own prefix rather than
/// generated, so re-running with an unchanged key recomputes the same hash and the seeder does nothing. A
/// changed key produces a different hash and rotates, which invalidates the previous key immediately.
/// </para>
/// <para>
/// <strong>Nothing here is logged but the outcome and the client's name.</strong> Not the key, not the
/// prefix, not the hash (external.md: credentials are redacted in logs).
/// </para>
/// </remarks>
internal sealed class OpsApiClientSeeder(
    IOpsApiClientDataLayer dataLayer,
    IOptions<OpsApiClientOptions> options,
    ILogger<OpsApiClientSeeder> logger) : IDataSeeder
{
    public string Name => "Ops API clients";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in options.Value.Clients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An entry with no key is one the deployment has not provisioned. Skipping is deliberate: the
            // AppHost declares the operator client unconditionally and leaves its key parameter empty, so a
            // clean clone starts with no operator credential and every ops route answers 401 — rather than
            // the host refusing to start until somebody invents a key they did not ask for.
            if (string.IsNullOrWhiteSpace(definition.Key))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new InvalidOperationException("Each Ops:Clients entry with a key also needs a Name.");
            }

            var unknown = definition.Scopes.Where(scope => !OpsScopes.All.Contains(scope)).ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Ops client '{definition.Name}' requests unknown scopes: {string.Join(", ", unknown)}.");
            }

            if (definition.Scopes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Ops client '{definition.Name}' grants no scopes; a key that permits nothing is a mistake, not a policy.");
            }

            var derived = OpsApiKeyHasher.Derive(definition.Key)
                ?? throw new InvalidOperationException(
                    $"Ops client '{definition.Name}' has a malformed key. The form is {OpsApiKeyPolicy.Prefix}<prefix>.<secret>.");

            var outcome = await dataLayer.UpsertAsync(
                definition.Name,
                derived.Prefix,
                derived.Salt,
                derived.Hash,
                string.Join(',', definition.Scopes),
                cancellationToken);

            if (outcome is not OpsApiClientSeedOutcome.Unchanged)
            {
                logger.LogInformation("Ops API client {ClientName}: {Outcome}.", definition.Name, outcome);
            }
        }
    }
}
