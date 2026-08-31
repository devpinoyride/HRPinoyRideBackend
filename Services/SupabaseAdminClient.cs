using System.Net.Http.Json;
using System.Text.Json;
using PinoyRideHrApi.Infrastructure;

namespace PinoyRideHrApi.Services;

/// <summary>
/// Talks to Supabase Auth's Admin API (service-role key only) to create/invite
/// users. Used by the HR admin when adding staff.
/// </summary>
public class SupabaseAdminClient
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _opts;
    private readonly ILogger<SupabaseAdminClient> _logger;

    public SupabaseAdminClient(HttpClient http, SupabaseOptions opts, ILogger<SupabaseAdminClient> logger)
    {
        _http = http;
        _opts = opts;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<Guid> CreateUserAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.Url) || string.IsNullOrWhiteSpace(_opts.ServiceRoleKey))
        {
            throw new ApiException(500, "SUPABASE_URL / SUPABASE_SERVICE_ROLE_KEY are not configured.");
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_opts.Url.TrimEnd('/')}/auth/v1/admin/users")
        {
            Content = JsonContent.Create(new { email, password, email_confirm = true })
        };
        request.Headers.Add("apikey", _opts.ServiceRoleKey);
        request.Headers.Add("Authorization", $"Bearer {_opts.ServiceRoleKey}");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Supabase admin create user failed: {Code} {Body}", response.StatusCode, body);
            throw new ApiException(400, ExtractErrorMessage(body, "Failed to create the auth user."));
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("id", out var idElement) &&
            Guid.TryParse(idElement.GetString(), out var userId))
        {
            return userId;
        }

        throw new ApiException(500, "Supabase did not return a user id for the new account.");
    }

    private static string ExtractErrorMessage(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            foreach (var key in new[] { "msg", "message", "error_description", "error" })
            {
                if (root.TryGetProperty(key, out var value))
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // ignore malformed responses from Supabase
        }

        return fallback;
    }
}