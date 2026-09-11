using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FinanceAi.Api.Http;

/// <summary>
/// RFC 9457 <c>application/problem+json</c> exactly as doc 05 §0.2 specifies.
/// <para>
/// <c>detail</c> is English, for logs and support. The client renders what the user reads from
/// <c>messageKey</c> in their own locale (API-04, UI-12): the backend never returns a display
/// sentence, because that would put translation in two places and guarantee drift.
/// </para>
/// </summary>
public static class ApiProblems
{
    public const string UnexpectedField = "unexpected_field";
    public const string ValidationFailed = "validation_failed";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string ConcurrencyConflict = "concurrency_conflict";
    public const string RateLimited = "rate_limited";
    public const string BusinessRuleViolated = "business_rule_violated";

    /// <summary>Doc 05 §0.2: <c>meta</c> carries the live figure that explains a refusal (e.g. the open balance).</summary>
    public sealed record FieldError(string Field, string Code, string MessageKey, IReadOnlyDictionary<string, string>? Meta = null);

    public static ProblemHttpResult Create(
        HttpContext context,
        int status,
        string code,
        string detail,
        string messageKey,
        IReadOnlyList<FieldError>? errors = null)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["messageKey"] = messageKey,
            ["traceId"] = Activity.Current?.Id ?? context?.TraceIdentifier,
        };

        if (errors is { Count: > 0 })
        {
            extensions["errors"] = errors;
        }

        return TypedResults.Problem(new ProblemDetails
        {
            Type = $"https://finance-ai/errors/{code}",
            Title = TitleFor(code),
            Status = status,
            // Deliberately never contains another tenant's data or a caller-supplied value that
            // could carry one (SEC-28).
            Detail = detail,
            Instance = context?.Request.Path.Value,
            Extensions = extensions,
        });
    }

    /// <summary>
    /// Cross-tenant access returns 404, never 403 (API-03, SEC-13): a 403 would confirm that the
    /// resource exists, and existence is itself information about another organization.
    /// </summary>
    public static ProblemHttpResult NotFoundProblem(HttpContext context) =>
        Create(context, StatusCodes.Status404NotFound, NotFound,
            "The requested resource does not exist.", "errors.not_found");

    public static ProblemHttpResult UnauthenticatedProblem(HttpContext context) =>
        Create(context, StatusCodes.Status401Unauthorized, Unauthenticated,
            "Authentication is required.", "errors.unauthenticated");

    public static ProblemHttpResult ForbiddenProblem(HttpContext context, string permission) =>
        Create(context, StatusCodes.Status403Forbidden, Forbidden,
            $"The authenticated user lacks the '{permission}' permission in this tenant.",
            "errors.forbidden");

    public static ProblemHttpResult ValidationProblem(
        HttpContext context, IReadOnlyList<FieldError> errors) =>
        Create(context, StatusCodes.Status400BadRequest, ValidationFailed,
            "The request body failed validation.", "errors.validation_failed", errors);

    public static ProblemHttpResult UnexpectedFieldProblem(HttpContext context, string? field) =>
        Create(context, StatusCodes.Status400BadRequest, UnexpectedField,
            field is null
                ? "The request body contains a field this endpoint does not accept."
                : $"The request body contains a field this endpoint does not accept: '{field}'.",
            "errors.unexpected_field",
            field is null ? null : [new FieldError(field, UnexpectedField, "errors.unexpected_field")]);

    /// <summary>422 with one field error carrying the rule's code and its explanatory figures.</summary>
    public static ProblemHttpResult BusinessRuleProblem(HttpContext context, string ruleCode, string? field, IReadOnlyDictionary<string, string>? meta) =>
        Create(context, StatusCodes.Status422UnprocessableEntity, BusinessRuleViolated,
            $"Business rule violated: {ruleCode}.", $"errors.rule.{ruleCode}",
            [new FieldError(field ?? string.Empty, ruleCode, $"errors.rule.{ruleCode}", meta)]);

    private static string TitleFor(string code) => code switch
    {
        UnexpectedField => "Unexpected field",
        ValidationFailed => "Validation failed",
        Unauthenticated => "Unauthenticated",
        Forbidden => "Forbidden",
        NotFound => "Not found",
        ConcurrencyConflict => "Concurrency conflict",
        RateLimited => "Rate limited",
        BusinessRuleViolated => "Business rule violated",
        _ => "Request failed",
    };
}
