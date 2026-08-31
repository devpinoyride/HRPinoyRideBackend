using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
}