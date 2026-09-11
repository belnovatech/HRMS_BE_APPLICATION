using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HRMS.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

            int status = exception switch
            {
                BadHttpRequestException bad => bad.StatusCode,
                ArgumentException => 400,
                UnauthorizedAccessException => 403,
                DbUpdateConcurrencyException => 409,
                DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } => 409,
                DbUpdateException { InnerException: PostgresException { SqlState: "40001" } } => 409,
                PostgresException { SqlState: "40001" } => 409,
                _ => 500
            };
            ProblemDetails problem = new()
            {
                Status = status,
                Title = status switch
                {
                    400 => "Invalid request.",
                    403 => "Access denied.",
                    409 => "The record conflicts with existing data or was changed. Reload and retry.",
                    _ => "An unexpected error occurred."
                },
                Detail = "Use the trace identifier when contacting support.",
                Instance = context.Request.Path
            };
            problem.Extensions["traceId"] = context.TraceIdentifier;

            context.Response.StatusCode = problem.Status.Value;
            await context.Response.WriteAsJsonAsync(problem);
        }
    }
}
