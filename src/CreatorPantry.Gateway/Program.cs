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

// A route that declares MaxRequestBodySize carries a body larger than the 4 MB default. YARP applies that number
// when it forwards, but the edge's own body limit runs first and reads endpoint metadata, so the route's limit is
// published there too; otherwise the default would refuse the request before YARP saw it. 21 MB on the two
// brand-source routes is BrandPolicy.SourceUploadRequestMaxBytes, and 9 MB on the extraction-correction route is
// BrandPolicy.ExtractionCorrectionRequestMaxBytes. The API enforces each again on its own action, which is what
// decides the refusal: the edge's job is to bound the body, not to answer for the domain.
app.MapReverseProxy().Add(endpoint =>
{
    if (endpoint.Metadata.OfType<RouteModel>().FirstOrDefault()?.Config.MaxRequestBodySize is { } limit)
    {
        endpoint.Metadata.Add(new RequestSizeLimitAttribute(limit));
    }
});

app.Run();
