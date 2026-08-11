using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CmCSP.Api;

/// <summary>
/// Options controlling the public REST API surface: the shared API key that external
/// callers must present and the header it is read from. Configured under the
/// <c>PublicApi</c> section (or the <c>PublicApi__ApiKey</c> environment variable /
/// <c>PublicApi--ApiKey</c> Key Vault secret) so the default key can be overridden per
/// deployment without a code change.
/// </summary>
public sealed class PublicApiOptions
{
    public const string SectionName = "PublicApi";

    /// <summary>HTTP header external clients send their API key in. Defaults to <c>X-API-Key</c>.</summary>
    public string HeaderName { get; set; } = "X-API-Key";

    /// <summary>
    /// The shared secret callers must present. A strong default is baked in so the API works
    /// out of the box; override it in production via <c>PublicApi:ApiKey</c>.
    /// </summary>
    public string ApiKey { get; set; } = "cmcsp_live_7Kq2Fxn9RpVe4sTgWmH3dYbA6uZcJ8Lw";
}

/// <summary>
/// Endpoint filter that rejects any request to the public API which does not carry a valid
/// API key in the configured header. Applied to the whole <c>/api</c> route group so every
/// data endpoint is protected uniformly. Uses a fixed-time comparison to avoid leaking the
/// key length/prefix through timing.
/// </summary>
public sealed class ApiKeyEndpointFilter(PublicApiOptions options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!http.Request.Headers.TryGetValue(options.HeaderName, out var provided) ||
            provided.Count == 0 ||
            !IsValid(provided.ToString(), options.ApiKey))
        {
            return Results.Problem(
                title: "Missing or invalid API key.",
                detail: $"Provide a valid key in the '{options.HeaderName}' request header.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    private static bool IsValid(string provided, string expected)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// Registers the API-key security scheme on the generated OpenAPI document and marks every
/// operation as requiring it, so the Scalar UI renders an "API key" field and includes the
/// header in its generated code samples / try-it requests.
/// </summary>
public sealed class ApiKeySecuritySchemeTransformer(PublicApiOptions options) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        const string schemeId = "ApiKey";
        var scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = options.HeaderName,
            Description = $"Shared API key. Send it in the '{options.HeaderName}' header on every request.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[schemeId] = scheme;

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(schemeId, document)] = []
        };

        foreach (var path in document.Paths.Values)
        {
            if (path.Operations is null) continue;
            foreach (var operation in path.Operations.Values)
                (operation.Security ??= []).Add(requirement);
        }

        return Task.CompletedTask;
    }
}
