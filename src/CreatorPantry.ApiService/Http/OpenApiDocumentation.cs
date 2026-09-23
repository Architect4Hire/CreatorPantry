using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Asp.Versioning;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace CreatorPantry.ApiService.Http;

/// <summary>
/// Versioned OpenAPI documents (<c>/openapi/v1.json</c>) and the Scalar reference (<c>/scalar</c>), mapped in
/// Development only. Shared API conventions are published as reusable components.
/// </summary>
public static class OpenApiDocumentation
{
    public const string SessionScheme = "bffSession";

    /// <summary>Provisional; prompt 1.8 finalizes the BFF session cookie.</summary>
    public const string SessionCookieName = "__Host-creatorpantry-session";

    public const string ProblemDetailsSchema = "ProblemDetails";
    public const string ValidationProblemDetailsSchema = "ValidationProblemDetails";
    public const string CursorPageSchema = "CursorPage";
    public const string IdempotencyKeyParameter = "IdempotencyKey";
    public const string CursorParameter = "Cursor";
    public const string LimitParameter = "Limit";
    public const string FileUploadRequestBody = "FileUpload";
    public const string AntiforgeryParameter = "AntiforgeryToken";

    public static IApiVersioningBuilder AddCreatorPantryOpenApi(this IApiVersioningBuilder builder) =>
        builder.AddOpenApi(options =>
        {
            options.Document.CreateSchemaReferenceId = PublicSchemaId;
            options.Document.AddSchemaTransformer(TransformSchemaAsync);
            options.Document.AddDocumentTransformer(TransformDocumentAsync);
            options.Document.AddOperationTransformer(TransformOperationAsync);
        });

    /// <summary>
    /// Describes enums as the strings the API actually writes.
    /// </summary>
    /// <remarks>
    /// The serializer is configured to write enum names, but the schema generator does not read that
    /// configuration and describes every enum as a bare <c>integer</c> — a document that contradicts its own
    /// responses. Stating it here keeps the two together, and publishes the member names, which the integer
    /// form never did: a generated client got an <c>int</c> and a private mapping to maintain.
    /// </remarks>
    private static Task TransformSchemaAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;

        if (type.IsEnum)
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = null;
            schema.Enum = [.. Enum.GetNames(type).Select(name => (JsonNode)JsonValue.Create(name))];
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The public name of a schema, which is deliberately not its C# type name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two problems with the default, both of which reach clients. Generic types are mangled into names like
    /// <c>CursorPageServiceModelOfIngredientServiceModel</c>, and every name carries the
    /// <c>ServiceModel</c>/<c>ViewModel</c> suffix — an internal layering convention that means nothing outside
    /// this codebase. A generated SDK emits a class per schema name, so renaming one afterwards is a breaking
    /// change for everyone who generated against it. Doing it now, while nothing has been generated, costs
    /// nothing.
    /// </para>
    /// <para>
    /// A page of ingredients becomes <c>IngredientPage</c>, an ingredient becomes <c>Ingredient</c>, and
    /// <c>CreateWorkspaceViewModel</c> becomes <c>CreateWorkspace</c>. Enums and the hand-written shared
    /// components are left exactly as they are.
    /// </para>
    /// </remarks>
    private static string? PublicSchemaId(JsonTypeInfo typeInfo)
    {
        var type = typeInfo.Type;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CursorPageServiceModel<>))
        {
            return $"{StripLayerSuffix(type.GetGenericArguments()[0].Name)}Page";
        }

        var generated = OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);

        return generated is null ? null : StripLayerSuffix(generated);
    }

    private static string StripLayerSuffix(string name)
    {
        foreach (var suffix in (string[])["ServiceModel", "ViewModel"])
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    public static WebApplication MapApiDocumentation(this WebApplication app)
    {
        // Not exposed outside Development without a separate, explicit approval.
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        app.MapOpenApi().WithDocumentPerVersion().AllowAnonymous();
        app.MapScalarApiReference(options => options.WithTitle("CreatorPantry API")).AllowAnonymous();

        return app;
    }

    private static Task TransformDocumentAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "CreatorPantry API";
        document.Info.Description =
            "Browser clients call these routes through CreatorPantry.Gateway with a session cookie. "
            + "Errors are RFC 9457 problem details carrying a stable `code` and a `traceId`.";

        // Never publish internal hosts; clients use the gateway URL from runtime configuration.
        document.Servers = [];

        document.Components ??= new OpenApiComponents();
        var components = document.Components;

        components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        components.SecuritySchemes[SessionScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = SessionCookieName,
            Description = "HttpOnly session cookie issued by the gateway (BFF). Browsers never handle tokens. "
                + "Provisional name, finalized with the BFF session.",
        };

        components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        components.Schemas[ProblemDetailsSchema] = ProblemSchema(validation: false);
        components.Schemas[ValidationProblemDetailsSchema] = ProblemSchema(validation: true);
        components.Schemas[CursorPageSchema] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Cursor-paginated collection envelope, and the shape every `*Page` schema in this "
                + "document follows. Pass `nextCursor` back as `cursor`; null means no more items.",
            Required = new HashSet<string> { "items", "nextCursor" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["items"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema() },
                ["nextCursor"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            },
        };

        components.Parameters ??= new Dictionary<string, IOpenApiParameter>();
        components.Parameters[IdempotencyKeyParameter] = new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Client-generated key, unique per logical command. Replaying a key returns the original "
                + "committed result instead of repeating the command.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 255 },
        };
        components.Parameters[AntiforgeryParameter] = new OpenApiParameter
        {
            Name = "X-XSRF-TOKEN",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Antiforgery request token from GET /bff/antiforgery (or the sign-in response). Required by the "
                + "gateway on every unsafe request; a missing or stale token is rejected with 400 csrf.invalid.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        };
        components.Parameters[CursorParameter] = new OpenApiParameter
        {
            Name = "cursor",
            In = ParameterLocation.Query,
            Description = "Opaque cursor from a previous page's `nextCursor`. Omit for the first page. Valid only for "
                + "the same resource and the same filters that produced it; changing a filter mid-page means "
                + "starting again without a cursor.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        };
        components.Parameters[LimitParameter] = new OpenApiParameter
        {
            Name = "limit",
            In = ParameterLocation.Query,
            // Bounds come from ReferencePolicy rather than literals: the policy's own remarks claim the
            // contract and the implementation cannot drift apart, and that is only true if one reads the other.
            Description = $"Maximum items per page. Values outside {ReferencePolicy.MinPageSize}-{ReferencePolicy.MaxPageSize} "
                + "are clamped to the nearest bound, not rejected.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                Minimum = ReferencePolicy.MinPageSize.ToString(CultureInfo.InvariantCulture),
                Maximum = ReferencePolicy.MaxPageSize.ToString(CultureInfo.InvariantCulture),
                Default = JsonValue.Create(ReferencePolicy.DefaultPageSize),
            },
        };

        components.RequestBodies ??= new Dictionary<string, IOpenApiRequestBody>();
        components.RequestBodies[FileUploadRequestBody] = new OpenApiRequestBody
        {
            Required = true,
            Description = "Single-file upload. The type is verified by file signature, not extension, and size is capped.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "file" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                        },
                    },
                },
            },
        };

        return Task.CompletedTask;
    }

    private static Task TransformOperationAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (!metadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SessionScheme, context.Document)] = [] },
            ];
        }

        var method = context.Description.HttpMethod ?? string.Empty;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameterReference(AntiforgeryParameter, context.Document));
        }

        UseSharedPagingParameters(operation, context);

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["default"] = new OpenApiResponse
        {
            Description = "Problem details with a stable `code` and `traceId`.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new()
                {
                    Schema = new OpenApiSchemaReference(ProblemDetailsSchema, context.Document),
                },
            },
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Points an operation's <c>cursor</c> and <c>limit</c> query parameters at the published components
    /// instead of the shapes the generator inferred from the ViewModel.
    /// </summary>
    /// <remarks>
    /// The inferred shapes are wrong in a way that matters to a client: a nullable <c>int</c> bound from a
    /// query string is generated as <c>["integer", "string"]</c> with a regex, and it carries none of the
    /// bounds. The components say what the contract actually is — int32, 1 to 100, default 25 — and every
    /// paginated route now says it the same way, because it says it in one place.
    /// </remarks>
    private static void UseSharedPagingParameters(
        OpenApiOperation operation, OpenApiOperationTransformerContext context)
    {
        if (operation.Parameters is null)
        {
            return;
        }

        for (var index = 0; index < operation.Parameters.Count; index++)
        {
            var parameter = operation.Parameters[index];
            if (parameter.In != ParameterLocation.Query)
            {
                continue;
            }

            switch (parameter.Name)
            {
                case "cursor" or "Cursor":
                    operation.Parameters[index] = new OpenApiParameterReference(CursorParameter, context.Document);
                    break;
                case "limit" or "Limit":
                    operation.Parameters[index] = new OpenApiParameterReference(LimitParameter, context.Document);
                    break;
                default:
                    // A query parameter bound from a complex type inherits the action's summary when it has no
                    // description of its own, which documents every filter as though it were the endpoint.
                    // Saying nothing is better than saying something false; [Description] is how a parameter
                    // gets a real one.
                    if (parameter is OpenApiParameter concrete
                        && concrete.Description == operation.Summary
                        && operation.Summary is not null)
                    {
                        concrete.Description = null;
                    }

                    break;
            }
        }
    }

    private static OpenApiSchema ProblemSchema(bool validation)
    {
        var properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer | JsonSchemaType.Null, Format = "int32" },
            ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["code"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Stable, machine-readable error code, e.g. `auth.registration.invalid` or `not_found`.",
            },
            ["traceId"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Correlation id for support and tracing.",
            },
        };

        if (validation)
        {
            properties["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Field errors keyed by camelCase request field name.",
                AdditionalProperties = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            };
        }

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string> { "code", "traceId" },
            Properties = properties,
        };
    }
}
