using CreatorPantry.Gateway.InternalTokens;
using CreatorPantry.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Model;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The only credential the API accepts is a short-lived token minted here (baseline B-13).
builder.Services.AddInternalTokens(builder.Configuration);

// The browser session: Redis-backed cookie session, antiforgery, CORS for the SPA, login rate limit.
builder.AddBffSession();
builder.AddTrustedProxies();

// Routes, header transforms, and the "https+http://api" destination live in appsettings.json (ReverseProxy).
// Aspire service discovery resolves the destination; no host or port is configured here.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver()
    .AddTrustedHeaderTransform()   // drop every caller header outside the allowlist...
    .AddInternalTokenTransform();  // ...then add the only credential the API accepts

var app = builder.Build();

app.UseEdgeSecurity();

app.MapDefaultEndpoints();
app.MapBffEndpoints();

// A route that declares MaxRequestBodySize is an upload route. YARP applies that number when it forwards, but
// the edge's own body limit runs first and reads endpoint metadata, so the route's limit is published there
// too; otherwise the 4 MB default would refuse the upload before YARP saw it. 21 MB on the brand-source route
// is BrandPolicy.SourceUploadRequestMaxBytes, which the API enforces again on its own action.
app.MapReverseProxy().Add(endpoint =>
{
    if (endpoint.Metadata.OfType<RouteModel>().FirstOrDefault()?.Config.MaxRequestBodySize is { } limit)
    {
        endpoint.Metadata.Add(new RequestSizeLimitAttribute(limit));
    }
});

app.Run();
