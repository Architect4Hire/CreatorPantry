using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Gateway.InternalTokens;

namespace CreatorPantry.Gateway.Sessions;

/// <summary>A session user as returned by the API's internal session routes.</summary>
public sealed record SessionUser(string UserId, string DisplayName, IReadOnlyList<string> Roles, string SecurityStamp);

/// <summary>Either the session user, or the API's stable rejection code and title.</summary>
public sealed record ApiSessionResult(SessionUser? User, string? ErrorCode, string? ErrorTitle);

/// <summary>
/// Calls the API's internal session routes with the gateway's service token. Configured without retries:
/// repeating a credential check could double-count failed attempts toward lockout.
/// </summary>
public sealed class ApiSessionClient(HttpClient http, IInternalTokenIssuer tokens)
{
    /// <summary>The code used when the API refuses to verify a session at all (401/403/404).</summary>
    public const string SessionUnverifiableCode = "auth.session.unverifiable";

    public Task<ApiSessionResult> VerifyCredentialsAsync(string email, string password, CancellationToken cancellationToken) =>
        PostAsync("api/v1/internal/sessions", new { email, password }, failClosed: false, cancellationToken);

    /// <summary>
    /// Fails closed: if the API rejects the gateway (401/403) or the route is missing (404), the session is
    /// reported invalid rather than kept, so a misconfiguration cannot silently disable revocation.
    /// </summary>
    public Task<ApiSessionResult> ValidateSessionAsync(string userId, string securityStamp, CancellationToken cancellationToken) =>
        PostAsync("api/v1/internal/sessions/validate", new { userId, securityStamp }, failClosed: true, cancellationToken);

    /// <exception cref="HttpRequestException">The API is unavailable or answered unexpectedly.</exception>
    private async Task<ApiSessionResult> PostAsync(string path, object body, bool failClosed, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.IssueService());

        using var response = await http.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var user = await response.Content.ReadFromJsonAsync<SessionUser>(cancellationToken)
                ?? throw new HttpRequestException("The API returned an empty session user.");
            return new ApiSessionResult(user, null, null);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return new ApiSessionResult(
                null,
                problem.TryGetProperty("code", out var code) ? code.GetString() : null,
                problem.TryGetProperty("title", out var title) ? title.GetString() : null);
        }

        if (failClosed && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return new ApiSessionResult(null, SessionUnverifiableCode, "The session could not be verified.");
        }

        throw new HttpRequestException($"The API session route answered {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
