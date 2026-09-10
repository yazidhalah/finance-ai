using System.Text.Json;
using FinanceAi.Api.Http;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Middleware;

/// <summary>
/// Turns the failures that reach the pipeline into the problem shapes of doc 05 §0.2, and keeps
/// exception detail out of the response — a stack trace or a database message can carry another
/// tenant's data (SEC-28).
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (ex.InnerException is JsonException json)
        {
            await this.WriteAsync(context, MapJsonFailure(context, json), ex);
        }
        catch (JsonException json)
        {
            await this.WriteAsync(context, MapJsonFailure(context, json), json);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await this.WriteAsync(
                context,
                ApiProblems.Create(
                    context, StatusCodes.Status409Conflict, ApiProblems.ConcurrencyConflict,
                    "The resource was modified by someone else since it was read.",
                    "errors.concurrency_conflict"),
                ex);
        }
    }

    /// <summary>
    /// <c>JsonUnmappedMemberHandling.Disallow</c> makes an unknown field a hard failure rather than
    /// something silently dropped. That is what implements SEC-17 and API-01 at once: a body
    /// carrying <c>tenantId</c> is rejected rather than ignored, so a client can never believe it
    /// chose a tenant.
    /// </summary>
    private static IResult MapJsonFailure(HttpContext context, JsonException json)
    {
        var isUnmappedMember =
            json.Message.Contains("could not be mapped", StringComparison.Ordinal) ||
            json.Message.Contains("unmapped", StringComparison.OrdinalIgnoreCase);

        if (!isUnmappedMember)
        {
            return ApiProblems.Create(
                context, StatusCodes.Status400BadRequest, ApiProblems.ValidationFailed,
                "The request body could not be parsed.", "errors.validation_failed");
        }

        // "$.tenantId" -> "tenantId". Echoing the field name back is safe: it came from the caller.
        var field = json.Path?.TrimStart('$', '.');
        return ApiProblems.UnexpectedFieldProblem(context, string.IsNullOrEmpty(field) ? null : field);
    }

    private async Task WriteAsync(HttpContext context, IResult problem, Exception ex)
    {
        if (context.Response.HasStarted)
        {
            logger.LogError(ex, "Request failed after the response had started.");
            throw ex;
        }

        logger.LogWarning(ex, "Request rejected: {ExceptionType}", ex.GetType().Name);
        context.Response.Clear();
        await problem.ExecuteAsync(context);
    }
}
