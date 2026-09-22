using CreatorPantry.ServiceDefaults;
using CreatorPantry.Web;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOptions<ClientOptions>()
    .Bind(builder.Configuration.GetSection(ClientOptions.SectionName))
    .Validate(options => ClientOptions.IsAbsoluteHttpUrl(options.GatewayUrl),
        "Client:GatewayUrl must be an absolute http(s) URL.")
    .ValidateOnStart();

var app = builder.Build();

app.UseSecurityHeaders(app.Services.GetRequiredService<IOptions<ClientOptions>>().Value.SpaContentSecurityPolicy());

app.MapDefaultEndpoints();

app.UseStaticFiles();

app.MapGet("/runtime-config.json", (IOptions<ClientOptions> options, HttpResponse response) =>
{
    response.Headers.CacheControl = "no-store";
    return TypedResults.Ok(new RuntimeConfig(options.Value.GatewayUrl));
});

// Deep links resolve to the SPA. index.html is revalidated so a new deployment's hashed bundles load.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();
