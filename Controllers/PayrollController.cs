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