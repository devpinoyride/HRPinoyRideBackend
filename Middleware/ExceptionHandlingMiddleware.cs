using System.Text.Json;
using Npgsql;
using PinoyRideHrApi.Infrastructure;

namespace PinoyRideHrApi.Middleware;

/// <summary>
/// Global exception handler. Converts every unhandled exception into a
/// ProblemDetails-style JSON body ({ "error": "..." }) with a correct
/// status code so raw Npgsql/DB exceptions never reach the client.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("API error {StatusCode}: {Message}", ex.StatusCode, ex.Message);
            await WriteAsync(context, ex.StatusCode, ex.Message);
        }
        catch (PostgresException ex)
        {
            _logger.LogError(ex, "Database error [{SqlState}] {Message}", ex.SqlState, ex.Message);
            await WriteAsync(context, StatusFor(ex.SqlState), MessageFor(ex.SqlState));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            await WriteAsync(context, 500, "An unexpected error occurred.");
        }
    }

    private static int StatusFor(string sqlState) => sqlState switch
    {
        "23505" => 409,   // unique_violation
        "22P02" => 422,   // invalid_text_representation (bad enum / uuid)
        _ => 500
    };

    private static string MessageFor(string sqlState) => sqlState switch
    {
        "23505" => "A record with the same key already exists.",
        "22P02" => "One of the values provided is not valid for the request.",
        _ => "A database error occurred."
    };

    private static async Task WriteAsync(HttpContext context, int code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = code;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = JsonSerializer.Serialize(
            new { error = message },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(body);
    }
}