namespace PinoyRideHrApi.Models;

public class LoginRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

public class LoginResponse
{
    public string Token { get; set; } = "";
    public string? Role { get; set; }
    public string? FullName { get; set; }
}

public class ClockInRequest
{
    public string? WorkSetup { get; set; }
}

public class CreateRequestRequest
{
    public string? WorkDate { get; set; }
    public string? RequestedTimeIn { get; set; }
    public string? RequestedTimeOut { get; set; }
    public string? RequestType { get; set; }
    public string? Reason { get; set; }
}

public class ResolveRequestRequest
{
    public string? Notes { get; set; }
}

public class CreateStaffRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? FullName { get; set; }
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Role { get; set; }
    public Guid? ApproverId { get; set; }
    public decimal? BasicSalary { get; set; }
}

public class UpdateStaffRequest
{
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Role { get; set; }
    public Guid? ApproverId { get; set; }
    public decimal? BasicSalary { get; set; }
}