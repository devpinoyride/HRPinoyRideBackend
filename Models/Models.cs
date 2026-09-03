namespace PinoyRideHrApi.Models;

public class Profile
{
    public Guid Id { get; set; }
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public Guid? ApproverId { get; set; }
        public decimal? BasicSalary { get; set; }
    public string? SalaryMode { get; set; }        // 'basic' (default) or 'daily'
    public decimal? DailyRate { get; set; }
    // Per-staff payroll incentives (configurable; replaces hardcoded ₱100 constants).
    public bool OfficeIncentiveEnabled { get; set; } = true;
    public decimal OfficeIncentiveAmount { get; set; } = 100m;  // per office workday present
    public bool MobileIncentiveEnabled { get; set; } = true;
    public decimal MobileIncentiveAmount { get; set; } = 100m;  // per week with ≥1 workday
    public string WorkDays { get; set; } = "mon_fri";           // 'mon_fri' or 'mon_sat'
    public bool FixedSalary { get; set; }                       // basic mode: always full pay, no deduction
    public TimeOnly SchedTimeIn { get; set; } = new(9, 0);      // expected daily time-in
    public TimeOnly SchedTimeOut { get; set; } = new(17, 0);    // expected daily time-out
    public string? ApproverName { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public class TimeEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime? TimeIn { get; set; }
    public DateTime? TimeOut { get; set; }
    public string? Source { get; set; }
    public string? Status { get; set; }
    public string? WorkSetup { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public double? Hours { get; set; }
}

public class TimekeepingRequest
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public TimeOnly? RequestedTimeIn { get; set; }
    public TimeOnly? RequestedTimeOut { get; set; }
    public string? RequestType { get; set; }
    public string? Reason { get; set; }
    public string? LeaveDuration { get; set; }   // leave only: 'whole' | 'half_am' | 'half_pm'
    public string? WorkSetup { get; set; }        // adjustment/overtime: 'office' | 'wfh'
    public Guid? ApproverId { get; set; }
    public string? Status { get; set; }
    public string? ApproverNotes { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid? ActorId { get; set; }
    public string? Action { get; set; }
    public string? TableName { get; set; }
    public string? RecordId { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime? CreatedAt { get; set; }
}

// ---- Payroll (semi-monthly cutoffs: 11–25 and 26–10) ------------------------

public class PayrollPeriod
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int Cutoff { get; set; }     // 1 = 11–25, 2 = 26–10 (next month)
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
}

public class PayrollDayDetail
{
    public DateOnly Date { get; set; }
    public string? Weekday { get; set; }
    public string? Status { get; set; } // present | paid_leave | absent | upcoming
    public DateTime? TimeIn { get; set; }
    public DateTime? TimeOut { get; set; }
    public string? Source { get; set; }
    public string? WorkSetup { get; set; }
    public double? Hours { get; set; }
    public double? OvertimeHours { get; set; }
    // Punctuality (present days only): minutes late beyond grace, minutes left early.
    public int LateMinutes { get; set; }
    public int EarlyOutMinutes { get; set; }
}

public class PayrollComputation
{
    public string? SalaryMode { get; set; }        // 'basic' or 'daily'
    public string? WorkDayPattern { get; set; }    // 'mon_fri' or 'mon_sat'
    public bool FixedSalary { get; set; }
    public decimal BasicSalary { get; set; }
    public decimal DailyRate { get; set; }
    public decimal SemiMonthlyBasic { get; set; }
    public int Workdays { get; set; }
    public int WorkedDays { get; set; }
    public int PaidLeaveDays { get; set; }
    public int AbsentDays { get; set; }
    public decimal AbsenceDeduction { get; set; }
    public double OvertimeHours { get; set; }
    public decimal OvertimePay { get; set; }
    // Office incentive (per office workday present)
    public bool OfficeIncentiveEnabled { get; set; }
    public decimal OfficeIncentiveRate { get; set; }
    public int OfficeIncentiveDays { get; set; }
    public decimal OfficeAllowance { get; set; }
    // Mobile incentive (per week with a workday)
    public bool MobileIncentiveEnabled { get; set; }
    public decimal MobileIncentiveRate { get; set; }
    public int MobileIncentiveWeeks { get; set; }
    public decimal MobileAllowance { get; set; }
    // Sunday work (by request): flat +1 daily rate per approved Sunday worked.
    public int SundayDays { get; set; }
    public decimal SundayPay { get; set; }
    // Tardiness / undertime (late-in beyond 15-min grace + early-out), pro-rata.
    public int LateMinutes { get; set; }
    public int EarlyOutMinutes { get; set; }
    public decimal TardinessDeduction { get; set; }
    public decimal MinuteRate { get; set; }
    public decimal NetPay { get; set; }
}

public class PayrollPayslip
{
    public Profile Staff { get; set; } = null!;
    public PayrollPeriod Period { get; set; } = null!;
    public List<PayrollDayDetail> Days { get; set; } = new();
    public PayrollComputation? Computation { get; set; }
}

public class PayrollSummaryRow
{
    public Guid StaffId { get; set; }
    public string? FullName { get; set; }
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public string? SalaryMode { get; set; }
    public bool FixedSalary { get; set; }
    public decimal? BasicSalary { get; set; }
    public int Workdays { get; set; }
    public int WorkedDays { get; set; }
    public int PaidLeaveDays { get; set; }
    public int AbsentDays { get; set; }
    public decimal? DailyRate { get; set; }
    public decimal? SemiMonthlyBasic { get; set; }
    public decimal? AbsenceDeduction { get; set; }
    public double OvertimeHours { get; set; }
    public decimal? OvertimePay { get; set; }
    public decimal? OfficeAllowance { get; set; }
    public decimal? MobileAllowance { get; set; }
    public decimal? NetPay { get; set; }
}