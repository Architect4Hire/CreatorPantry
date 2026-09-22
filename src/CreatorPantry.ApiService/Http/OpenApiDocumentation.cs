using System.Text.Json.Nodes;
using Asp.Versioning;
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
            options.Document.AddDocumentTransformer(TransformDocumentAsync);
            options.Document.AddOperationTransformer(TransformOperationAsync);
        });

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
            Description = "Cursor-paginated collection envelope. Pass `nextCursor` back as `cursor`; null means no more items.",
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
            Description = "Opaque cursor from a previous page's `nextCursor`. Omit for the first page.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        };
        components.Parameters[LimitParameter] = new OpenApiParameter
        {
            Name = "limit",
            In = ParameterLocation.Query,
            Description = "Maximum items per page.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "100", Default = JsonValue.Create(25),
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
