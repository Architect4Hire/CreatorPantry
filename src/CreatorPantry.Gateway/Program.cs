using CreatorPantry.Gateway.InternalTokens;
using CreatorPantry.Gateway.Sessions;

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
app.MapReverseProxy();

app.Run();
