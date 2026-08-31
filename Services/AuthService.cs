using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.IdentityModel.Tokens;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Services;

/// <summary>
/// Verifies credentials against Supabase Auth, then issues this API's own
/// signed JWT (HS256) carrying sub, role, approver_id and full_name.
/// </summary>
public class AuthService
{
    private readonly SupabaseAuthClient _supabaseAuth;
    private readonly Db _db;
    private readonly JwtOptions _jwt;
    private readonly ILogger<AuthService> _logger;

    public AuthService(SupabaseAuthClient supabaseAuth, Db db, JwtOptions jwt, ILogger<AuthService> logger)
    {
        _supabaseAuth = supabaseAuth;
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var supabaseId = await _supabaseAuth.GetUserIdAsync(email, password, ct);
        if (supabaseId is null)
        {
            return null;
        }

        using var con = _db.Open();
        var profile = await con.QuerySingleOrDefaultAsync<Profile>(
            "select id, email, full_name, role, status, approver_id from profiles where id = @Id::uuid",
            new { Id = supabaseId });

        if (profile is null)
        {
            _logger.LogWarning("Supabase user {UserId} has no profiles row", supabaseId);
            return null;
        }

        if (string.Equals(profile.Status, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Login rejected: profile {UserId} is inactive", profile.Id);
            return null;
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, profile.Id.ToString()),
            new("role", string.IsNullOrWhiteSpace(profile.Role) ? "employee" : profile.Role),
            new("full_name", profile.FullName ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (profile.ApproverId.HasValue)
        {
            claims.Add(new Claim("approver_id", profile.ApproverId.Value.ToString()));
        }

        var issuedAt = DateTime.UtcNow;
        var credentials = new SigningCredentials(_jwt.Key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            _jwt.Issuer,
            _jwt.Audience,
            claims,
            issuedAt,
            issuedAt.AddHours(_jwt.ExpiryHours),
            credentials);

        return new LoginResponse
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            Role = profile.Role,
            FullName = profile.FullName
        };
    }
}