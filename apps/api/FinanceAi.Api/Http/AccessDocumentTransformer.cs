using FinanceAi.Api.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FinanceAi.Api.Http;

/// <summary>
/// Stamps every operation with the access it declares (slice 16, API-14): <c>x-permission</c> for a permissioned
/// route, <c>x-access</c> (<c>anonymous</c> / <c>authenticated</c>) otherwise, plus <c>x-allowed-without-mfa</c> and
/// <c>x-requires-reauthentication</c> where set — so a change to who may call what is a contract change the
/// snapshot diff shows. The document transformer names the API; the operation transformer reads each operation's
/// own endpoint metadata, the same objects the middleware enforces.
/// </summary>
public sealed class AccessDocumentTransformer : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    private const string ProblemSchema = "Problem";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Info.Title = "finance-ai AR & Collections API";
        document.Info.Version = "v1";

        // Slice 18: every failure is one shape (doc 05 §0.2) — RFC 9457 plus code, messageKey, traceId and field errors.
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas[ProblemSchema] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Doc 05 §0.2: an RFC 9457 problem. `code` is stable and machine-readable; `messageKey` is what the client localises; `traceId` correlates with the request log.",
            Required = new HashSet<string> { "type", "title", "status", "code", "messageKey" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
                ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["messageKey"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
                ["errors"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "field", "code", "messageKey" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["field"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["messageKey"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["meta"] = new OpenApiSchema { Type = JsonSchemaType.Object | JsonSchemaType.Null, AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.String } },
                        },
                    },
                },
            },
        };
        return Task.CompletedTask;
    }

    private static OpenApiResponse Problem(string description) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType>
        {
            ["application/problem+json"] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(ProblemSchema) },
        },
    };

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        var permission = metadata.OfType<RequiredPermission>().LastOrDefault();
        var access = metadata.OfType<DeclaredAccess>().LastOrDefault();
        if (permission is not null) operation.Extensions["x-permission"] = new JsonNodeExtension(permission.Permission);
        else if (access is not null) operation.Extensions["x-access"] = new JsonNodeExtension(access.Kind == DeclaredAccessKind.Anonymous ? "anonymous" : "authenticated");
        if (metadata.OfType<AllowedWithoutMfa>().Any()) operation.Extensions["x-allowed-without-mfa"] = new JsonNodeExtension(true);
        if (metadata.OfType<RequiresReauthentication>().Any()) operation.Extensions["x-requires-reauthentication"] = new JsonNodeExtension(true);

        // Slice 18: a download declares its media type without a CLR type; the generator then emits no content at all.
        operation.Responses ??= new OpenApiResponses();
        foreach (var produces in metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>())
        {
            var status = produces.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if ((produces.Type is null || produces.Type == typeof(void)) && produces.ContentTypes.Any() && operation.Responses.TryGetValue(status, out var response) && (response.Content is null || response.Content.Count == 0))
            {
                operation.Responses[status] = new OpenApiResponse
                {
                    Description = response.Description ?? "OK",
                    Content = produces.ContentTypes.ToDictionary(c => c, _ => new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }),
                };
            }
        }

        // Slice 22: a 201 always carries the new row's URL (TypedResults.Created sets it); the document says so.
        if (operation.Responses.TryGetValue("201", out var created) && created is OpenApiResponse createdResponse)
        {
            createdResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
            createdResponse.Headers["Location"] = new OpenApiHeader { Description = "The URL of the created resource.", Required = true, Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri-reference" } };
        }

        // The failure responses every operation can produce, all as the one problem shape.
        var anonymous = permission is null && access?.Kind == DeclaredAccessKind.Anonymous;
        if (!anonymous)
        {
            operation.Responses.TryAdd("401", Problem("No valid session (SEC-03), or MFA enrolment is required (403 mfa_enrollment_required is also possible)."));
        }

        if (permission is not null)
        {
            operation.Responses.TryAdd("403", Problem($"The caller's role does not hold '{permission.Permission}', or the session is not strong enough (mfa_enrollment_required, reauthentication_required)."));
        }

        if ((context.Description.RelativePath ?? string.Empty).Contains('{'))
        {
            operation.Responses.TryAdd("404", Problem("No such row in this tenant — the same answer whether it exists elsewhere or not at all (SEC-14)."));
        }

        if (context.Description.HttpMethod is "POST" or "PUT" or "PATCH")
        {
            operation.Responses.TryAdd("400", Problem("The request is malformed or fails validation; `errors` names each field."));
            operation.Responses.TryAdd("422", Problem("A business rule refused the request; `errors[0].code` is the rule (doc 03, doc 02)."));
        }

        operation.Responses.TryAdd("429", Problem("Rate limited (API-13); `Retry-After` is set."));
        return Task.CompletedTask;
    }
}
