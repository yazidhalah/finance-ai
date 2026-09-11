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
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Info.Title = "finance-ai AR & Collections API";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    }

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
        return Task.CompletedTask;
    }
}
