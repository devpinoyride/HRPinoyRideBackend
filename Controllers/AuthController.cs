using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;
using PinoyRideHrApi.Services;

namespace PinoyRideHrApi.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;

    public AuthController(AuthService auth)
    {
        _auth = auth;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return StatusCode(422, new { error = "Email and password are required." });
        }

        var result = await _auth.LoginAsync(request.Email.Trim(), request.Password, ct);
        if (result is null)
        {
            return StatusCode(401, new { error = "Invalid email or password, or the account is inactive." });
        }

        return Ok(result);
    }

    /// <summary>
    /// POST /auth/change-password — the signed-in user changes their own
    /// password after confirming their current one.
    /// </summary>
    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest? request, CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value;
        if (sub is null || !Guid.TryParse(sub, out var uid))
        {
            return StatusCode(401, new { error = "Unauthenticated." });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            return StatusCode(422, new { error = "currentPassword is required." });
        }
        var newPassword = request.NewPassword?.Trim();
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            return StatusCode(422, new { error = "newPassword must be at least 8 characters." });
        }

        var ok = await _auth.ChangePasswordAsync(uid, request.CurrentPassword, newPassword, ct);
        if (!ok)
        {
            return StatusCode(400, new { error = "Your current password is incorrect." });
        }

        return Ok(new { message = "Password changed successfully." });
    }
}