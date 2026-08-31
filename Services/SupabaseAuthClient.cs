using System.Net.Http.Json;
using System.Text.Json;
using PinoyRideHrApi.Infrastructure;

namespace PinoyRideHrApi.Services;

/// <summary>
/// Talks to Supabase Auth's REST token endpoint to verify an email/password
/// pair and return the Supabase user id.
/// </summary>
public class SupabaseAuthClient
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _opts;
    private readonly ILogger<SupabaseAuthClient> _logger;

    public SupabaseAuthClient(HttpClient http, SupabaseOptions opts, ILogger<SupabaseAuthClient> logger)
    {
        _http = http;
        _opts = opts;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<string?> GetUserIdAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.Url) || string.IsNullOrWhiteSpace(_opts.AnonKey))
        {
            throw new ApiException(500, "SUPABASE_URL / SUPABASE_ANON_KEY are not configured.");
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_opts.Url.TrimEnd('/')}/auth/v1/token?grant_type=password")
        {
            Content = JsonContent.Create(new { email, password })
        };
        request.Headers.Add("apikey", _opts.AnonKey);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Supabase auth token call failed: {Code} {Body}", response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("user", out var user) &&
            user.TryGetProperty("id", out var idElement))
        {
            return idElement.GetString();
        }

        return null;
    }
}