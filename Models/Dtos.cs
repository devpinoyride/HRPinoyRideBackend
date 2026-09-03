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

// HR admin resets a staff member's password to a new temporary one.
public class ResetPasswordRequest
{
    public string? NewPassword { get; set; }
}

// Employee changes their own password (verifies the current one first).
public class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

public class CreateRequestRequest
{
    public string? WorkDate { get; set; }
    public string? RequestedTimeIn { get; set; }
    public string? RequestedTimeOut { get; set; }
    public string? RequestType { get; set; }
    public string? Reason { get; set; }
    public string? LeaveDuration { get; set; }   // leave only: 'whole' | 'half_am' | 'half_pm'
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
    public string? SalaryMode { get; set; }        // 'basic' (default) or 'daily'
    public decimal? DailyRate { get; set; }
    public bool? OfficeIncentiveEnabled { get; set; }
    public decimal? OfficeIncentiveAmount { get; set; }
    public bool? MobileIncentiveEnabled { get; set; }
    public decimal? MobileIncentiveAmount { get; set; }
    public string? WorkDays { get; set; }              // 'mon_fri' or 'mon_sat'
    public bool? FixedSalary { get; set; }
    public string? SchedTimeIn { get; set; }           // "HH:mm"
    public string? SchedTimeOut { get; set; }          // "HH:mm"
}

public class UpdateStaffRequest
{
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Role { get; set; }
    public Guid? ApproverId { get; set; }
    public decimal? BasicSalary { get; set; }
    public string? SalaryMode { get; set; }        // 'basic' (default) or 'daily'
    public decimal? DailyRate { get; set; }
    public bool? OfficeIncentiveEnabled { get; set; }
    public decimal? OfficeIncentiveAmount { get; set; }
    public bool? MobileIncentiveEnabled { get; set; }
    public decimal? MobileIncentiveAmount { get; set; }
    public string? WorkDays { get; set; }              // 'mon_fri' or 'mon_sat'
    public bool? FixedSalary { get; set; }
    public string? SchedTimeIn { get; set; }           // "HH:mm"
    public string? SchedTimeOut { get; set; }          // "HH:mm"
}