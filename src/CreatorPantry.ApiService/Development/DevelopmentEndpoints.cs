using CreatorPantry.Domain.Gateways.AccountMessages;

namespace CreatorPantry.ApiService.Development;

/// <summary>A development-sink message as shown to a developer. Includes the reset token.</summary>
public sealed record DevelopmentAccountMessage(
    string Kind, string RecipientEmail, string? Token, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// Local-development tooling. Mapped only in the Development environment, where account messages go to
/// <see cref="InMemoryAccountMessageSink"/> instead of a delivery provider.
/// </summary>
public static class DevelopmentEndpoints
{
    // Deliberately outside /api: the gateway proxies only /api/**, so this route is never browser-reachable
    // through the gateway, even in Development.
    public const string AccountMessagesRoute = "/_dev/account-messages";

    public static WebApplication MapDevelopmentEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        app.MapGet(AccountMessagesRoute, (InMemoryAccountMessageSink sink, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                return sink.Messages.Select(message => new DevelopmentAccountMessage(
                    message.Kind.ToString(), message.RecipientEmail, message.Token, message.CreatedAt, message.ExpiresAt));
            })
            .ExcludeFromDescription()
            .AllowAnonymous();

        return app;
    }
}
