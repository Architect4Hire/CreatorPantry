using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace CreatorPantry.Tests.Gateway;

/// <summary>
/// Every route the gateway gives a body limit of its own is one the API accepts the same body on, and the other way
/// round (15.2) — raised for an upload, or lowered, as the payment provider's webhook is (AP.6.2).
/// </summary>
/// <remarks>
/// The numbers live in two places — the gateway's route configuration and the API action's
/// <c>[RequestSizeLimit]</c> — because the edge has to bound a body before YARP forwards it. Hand-copied numbers
/// drift: a gateway limit below the API's refuses uploads the domain would take, and one above it holds bodies at
/// the edge that the API will refuse anyway. This reads both and compares them.
/// </remarks>
public sealed partial class UploadRouteLimitDriftTests
{
    [Fact]
    public void The_gateway_and_the_api_agree_on_every_raised_body_limit()
    {
        var gateway = GatewayRoutes();
        var api = ApiActions();

        // Non-vacuous: the DAM and brand upload routes that exist today are both found on both sides, and so is the
        // one limit below the default.
        Assert.Contains(gateway, route => route.Path.EndsWith("/dam-assets", StringComparison.Ordinal));
        Assert.Contains(api, route => route.Path.EndsWith("/brand-source-documents", StringComparison.Ordinal));
        Assert.Contains(api, route => route.Path == "/api/v1/webhooks/stripe" && route.Bytes < EdgeDefault);

        Assert.Equal(api.Order(), gateway.Order());
    }

    private static List<RaisedLimit> GatewayRoutes()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(SourceRoot(), "CreatorPantry.Gateway", "appsettings.json")));
        var routes = settings.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");

        return
        [
            .. routes.EnumerateObject()
                .Where(route => route.Value.TryGetProperty("MaxRequestBodySize", out _))
                .SelectMany(route => route.Value.GetProperty("Match").GetProperty("Methods").EnumerateArray().Select(method =>
                    new RaisedLimit(
                        method.GetString()!.ToUpperInvariant(),
                        route.Value.GetProperty("Match").GetProperty("Path").GetString()!,
                        route.Value.GetProperty("MaxRequestBodySize").GetInt64()))),
        ];
    }

    private static List<RaisedLimit> ApiActions()
    {
        var api = Assembly.Load(new AssemblyName("CreatorPantry.ApiService"));

        return
        [
            .. api.GetTypes()
                .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
                .SelectMany(controller => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(action => (controller, action)))
                .Select(pair => (pair.controller, pair.action, limit: pair.action.GetCustomAttributes()
                    .OfType<IRequestSizeLimitMetadata>()
                    .FirstOrDefault()?.MaxRequestBodySize))
                // Any limit an action states. One equal to the edge's default would need no route of its own, so it
                // is the one case left out.
                .Where(entry => entry.limit is { } bytes && bytes != EdgeDefault)
                .SelectMany(entry => entry.action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(verb =>
                    verb.HttpMethods.Select(method => new RaisedLimit(
                        method,
                        Normalise(entry.controller.GetCustomAttribute<RouteAttribute>()!.Template, verb.Template),
                        entry.limit!.Value)))),
        ];
    }

    /// <summary>The controller and action templates as the gateway writes the path: v1, and no route constraints.</summary>
    private static string Normalise(string controllerTemplate, string? actionTemplate)
    {
        var path = "/" + controllerTemplate.Replace("v{version:apiVersion}", "v1", StringComparison.Ordinal)
            + (string.IsNullOrEmpty(actionTemplate) ? string.Empty : "/" + actionTemplate);

        return ConstraintPattern().Replace(path, "{$1}");
    }

    private const long EdgeDefault = 4L * 1024 * 1024;

    private sealed record RaisedLimit(string Method, string Path, long Bytes) : IComparable<RaisedLimit>
    {
        public int CompareTo(RaisedLimit? other) =>
            string.CompareOrdinal($"{Path} {Method} {Bytes}", other is null ? null : $"{other.Path} {other.Method} {other.Bytes}");
    }

    [GeneratedRegex(@"\{([A-Za-z]+):[^}]+\}")]
    private static partial Regex ConstraintPattern();

    private static string SourceRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
