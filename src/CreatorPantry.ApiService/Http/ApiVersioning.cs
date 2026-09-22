using Asp.Versioning;

namespace CreatorPantry.ApiService.Http;

/// <summary>
/// URL-segment API versioning: product routes are <c>api/v{version:apiVersion}/...</c> and a request must
/// name its version. Because the version is part of the resource's URL, an unversioned, unsupported, or
/// malformed version is a 404 (the library's URL-segment semantics). Health and development tooling routes
/// are not versioned.
/// </summary>
public static class ApiVersioning
{
    public static readonly ApiVersion V1 = new(1, 0);

    public static IServiceCollection AddCreatorPantryApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = V1;
                options.AssumeDefaultVersionWhenUnspecified = false;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
                options.ReportApiVersions = true;
            })
            .AddMvc()
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddCreatorPantryOpenApi();

        return services;
    }
}
