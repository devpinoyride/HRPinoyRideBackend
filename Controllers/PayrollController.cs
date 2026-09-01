using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;
using PinoyRideHrApi.Services;

namespace PinoyRideHrApi.Controllers;

[ApiController]
[Route("api/payroll")]
[Authorize]
public class PayrollController : ControllerBase
{
    private readonly Db _db;
    private readonly PayrollService _payroll;

    public PayrollController(Db db, PayrollService payroll)
    {
        _db = db;
        _payroll = payroll;
    }

    private Guid CurrentUserId()
    {
        var value = User.FindFirst("sub")?.Value;
        if (value is null || !Guid.TryParse(value, out var id))
        {
            throw new ApiException(401, "Unauthenticated.");
        }
        return id;
    }

    private bool IsHrAdmin() => User.FindFirst("role")?.Value == "hr_admin";

    /// <summary>
    /// GET /api/payroll/summary?year=&amp;month=&amp;cutoff= — attendance counts and
    /// computed pay for every staff member in the chosen cutoff. HR admin only.
    /// </summary>
    [HttpGet("summary")]
    [Authorize(Policy = "HrAdmin")]
    public async Task<IActionResult> Summary([FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? cutoff)
    {
        var today = PhClock.Today;
        var period = PayrollService.ResolvePeriod(year ?? today.Year, month ?? today.Month, cutoff ?? PayrollService.DefaultCutoff(today));

        var staff = await _payroll.GetStaffAsync();
        var rows = new List<PayrollSummaryRow>(staff.Count);
        foreach (var person in staff)
        {
            var slip = await _payroll.ComputeAsync(person, period);
            rows.Add(new PayrollSummaryRow
            {
                StaffId = person.Id,
                FullName = person.FullName,
                Department = person.Department,
                Position = person.Position,
                Role = person.Role,
                Status = person.Status,
                SalaryMode = person.SalaryMode ?? "basic",
                FixedSalary = person.FixedSalary && (person.SalaryMode ?? "basic") == "basic",
                BasicSalary = person.BasicSalary,
                Workdays = slip.Computation?.Workdays ?? 0,
                WorkedDays = slip.Computation?.WorkedDays ?? 0,
                PaidLeaveDays = slip.Computation?.PaidLeaveDays ?? 0,
                AbsentDays = slip.Computation?.AbsentDays ?? 0,
                DailyRate = slip.Computation?.DailyRate,
                SemiMonthlyBasic = slip.Computation?.SemiMonthlyBasic,
                AbsenceDeduction = slip.Computation?.AbsenceDeduction,
                OvertimeHours = slip.Computation?.OvertimeHours ?? 0,
                OvertimePay = slip.Computation?.OvertimePay,
                OfficeAllowance = slip.Computation?.OfficeAllowance,
                MobileAllowance = slip.Computation?.MobileAllowance,
                NetPay = slip.Computation?.NetPay
            });
        }

        return Ok(new { period, rows });
    }

    /// <summary>
    /// GET /api/payroll/export?year=&amp;month=&amp;cutoff= — bulk payroll for the
    /// chosen cutoff as a CSV file (one row per staff member). HR admin only.
    /// Mirrors the summary computation and includes the incentive breakdown.
    /// </summary>
    [HttpGet("export")]
    [Authorize(Policy = "HrAdmin")]
    public async Task<IActionResult> Export([FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? cutoff)
    {
        var today = PhClock.Today;
        var period = PayrollService.ResolvePeriod(year ?? today.Year, month ?? today.Month, cutoff ?? PayrollService.DefaultCutoff(today));

        var staff = await _payroll.GetStaffAsync();

        var sb = new StringBuilder();
        // Human-readable header block so the exported file documents the cutoff.
        sb.Append("Pinoy Ride — Payroll ").Append(period.Cutoff == 1 ? "Cutoff 1 (11–25)" : "Cutoff 2 (26–10)")
          .Append(' ').Append(period.Start.ToString("yyyy-MM-dd")).Append(" to ").Append(period.End.ToString("yyyy-MM-dd"))
          .AppendLine();
        sb.AppendLine();
        sb.AppendLine("Employee,Email,Department,Position,Role,Status,SalaryMode,BasicSalary,DailyRate,Workdays,DaysWorked,PaidLeaveDays,AbsentDays,SemiMonthlyBasic,AbsenceDeduction,OvertimeHours,OvertimePay,OfficeIncentive,MobileIncentive,SundayDays,SundayPay,NetPay");

        decimal totalNet = 0m;
        foreach (var person in staff)
        {
            var slip = await _payroll.ComputeAsync(person, period);
            var c = slip.Computation;
            totalNet += c?.NetPay ?? 0m;

            sb.Append(Csv(person.FullName)).Append(',')
              .Append(Csv(person.Email)).Append(',')
              .Append(Csv(person.Department)).Append(',')
              .Append(Csv(person.Position)).Append(',')
              .Append(Csv(person.Role)).Append(',')
              .Append(Csv(person.Status)).Append(',')
              .Append(Csv(c?.SalaryMode ?? person.SalaryMode ?? "basic")).Append(',')
              .Append(Csv(person.BasicSalary?.ToString("0.00"))).Append(',')
              .Append(Csv(c is null ? "" : c.DailyRate.ToString("0.00"))).Append(',')
              .Append(Csv((c?.Workdays ?? 0).ToString())).Append(',')
              .Append(Csv((c?.WorkedDays ?? 0).ToString())).Append(',')
              .Append(Csv((c?.PaidLeaveDays ?? 0).ToString())).Append(',')
              .Append(Csv((c?.AbsentDays ?? 0).ToString())).Append(',')
              .Append(Csv(c is null ? "" : c.SemiMonthlyBasic.ToString("0.00"))).Append(',')
              .Append(Csv(c is null ? "" : c.AbsenceDeduction.ToString("0.00"))).Append(',')
              .Append(Csv((c?.OvertimeHours ?? 0).ToString("0.##"))).Append(',')
              .Append(Csv(c is null ? "" : c.OvertimePay.ToString("0.00"))).Append(',')
              .Append(Csv(c is null ? "" : c.OfficeAllowance.ToString("0.00"))).Append(',')
              .Append(Csv(c is null ? "" : c.MobileAllowance.ToString("0.00"))).Append(',')
              .Append(Csv((c?.SundayDays ?? 0).ToString())).Append(',')
              .Append(Csv(c is null ? "" : c.SundayPay.ToString("0.00"))).Append(',')
              .Append(Csv(c is null ? "" : c.NetPay.ToString("0.00")))
              .AppendLine();
        }

        // Trailing total row for quick reconciliation.
        sb.AppendLine();
        sb.Append("TOTAL NET PAY,,,,,,,,,,,,,,,,,,,,,").Append(totalNet.ToString("0.00")).AppendLine();

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"payroll-{period.Year:D4}{period.Month:D2}-cutoff{period.Cutoff}.csv";
        return File(bytes, "text/csv", fileName);
    }

    /// <summary>
    /// GET /api/payroll/attendance-export?year=&amp;month=&amp;cutoff= — bulk attendance
    /// detail for the chosen cutoff as a CSV (one row per staff member per workday).
    /// HR admin only.
    /// </summary>
    [HttpGet("attendance-export")]
    [Authorize(Policy = "HrAdmin")]
    public async Task<IActionResult> AttendanceExport([FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? cutoff)
    {
        var today = PhClock.Today;
        var period = PayrollService.ResolvePeriod(year ?? today.Year, month ?? today.Month, cutoff ?? PayrollService.DefaultCutoff(today));

        var staff = await _payroll.GetStaffAsync();

        var sb = new StringBuilder();
        sb.Append("Pinoy Ride — Attendance ").Append(period.Cutoff == 1 ? "Cutoff 1 (11–25)" : "Cutoff 2 (26–10)")
          .Append(' ').Append(period.Start.ToString("yyyy-MM-dd")).Append(" to ").Append(period.End.ToString("yyyy-MM-dd"))
          .AppendLine();
        sb.AppendLine();
        sb.AppendLine("Employee,Email,Department,WorkDate,Weekday,Status,TimeIn,TimeOut,Hours,OvertimeHours,Setup");

        foreach (var person in staff)
        {
            var slip = await _payroll.ComputeAsync(person, period);
            foreach (var d in slip.Days)
            {
                sb.Append(Csv(person.FullName)).Append(',')
                  .Append(Csv(person.Email)).Append(',')
                  .Append(Csv(person.Department)).Append(',')
                  .Append(Csv(d.Date.ToString("yyyy-MM-dd"))).Append(',')
                  .Append(Csv(d.Weekday)).Append(',')
                  .Append(Csv(d.Status)).Append(',')
                  .Append(Csv(d.TimeIn?.ToString("yyyy-MM-dd HH:mm"))).Append(',')
                  .Append(Csv(d.TimeOut?.ToString("yyyy-MM-dd HH:mm"))).Append(',')
                  .Append(Csv(d.Hours?.ToString("0.00"))).Append(',')
                  .Append(Csv(d.OvertimeHours?.ToString("0.00"))).Append(',')
                  .Append(Csv(d.WorkSetup))
                  .AppendLine();
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"attendance-{period.Year:D4}{period.Month:D2}-cutoff{period.Cutoff}.csv";
        return File(bytes, "text/csv", fileName);
    }

    private static string Csv(object? value)
    {
        var s = value?.ToString() ?? "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
        {
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    /// <summary>
    /// GET /api/payroll/payslip?staffId=&amp;year=&amp;month=&amp;cutoff= — one staff
    /// member's payslip. HR admins may view anyone; approvers their assigned
    /// staff; everyone else only their own.
    /// </summary>
    [HttpGet("payslip")]
    public async Task<IActionResult> Payslip([FromQuery] Guid? staffId, [FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? cutoff)
    {
        var uid = CurrentUserId();
        var today = PhClock.Today;
        var period = PayrollService.ResolvePeriod(year ?? today.Year, month ?? today.Month, cutoff ?? PayrollService.DefaultCutoff(today));
        var target = staffId ?? uid;

        if (!IsHrAdmin() && target != uid)
        {
            using var con = _db.Open();
            var isAssigned = await con.QuerySingleOrDefaultAsync<Guid?>(
                "select id from profiles where id = @Target::uuid and approver_id = @Uid::uuid",
                new { Target = target, Uid = uid });
            if (isAssigned is null)
            {
                throw new ApiException(403, "You can only view payslips for your own account or your assigned staff.");
            }
        }

        var staff = await _payroll.GetStaffAsync(target);
        if (staff is null)
        {
            throw new ApiException(404, "Staff member not found.");
        }

        var slip = await _payroll.ComputeAsync(staff, period);
        return Ok(slip);
    }
}