using Microsoft.IdentityModel.Tokens;

namespace PinoyRideHrApi.Infrastructure;

public class JwtOptions
{
    public string Issuer { get; set; } = "PinoyRideHrApi";
    public string Audience { get; set; } = "PinoyRideHrFrontend";
    public SymmetricSecurityKey Key { get; set; } = null!;
    public int ExpiryHours { get; set; } = 8;
}