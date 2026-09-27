using System.Globalization;
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

        var finalized = await _payroll.IsFinalizedAsync(period);

        // A finalized cutoff renders from frozen snapshots, not a live recompute.
        var slips = finalized
            ? await _payroll.GetSnapshotsAsync(period)
            : null;

        var rows = new List<PayrollSummaryRow>();
        if (finalized && slips is not null)
        {
            foreach (var slip in slips)
            {
                var c = slip.Computation;
                rows.Add(new PayrollSummaryRow
                {
                    StaffId = slip.Staff.Id,
                    FullName = slip.Staff.FullName,
                    Department = slip.Staff.Department,
                    Position = slip.Staff.Position,
                    Role = slip.Staff.Role,
                    Status = slip.Staff.Status,
                    SalaryMode = c?.SalaryMode ?? "basic",
                    FixedSalary = c?.FixedSalary ?? false,
                    BasicSalary = c is null ? null : c.BasicSalary,
                    Workdays = c?.Workdays ?? 0,
                    WorkedDays = c?.WorkedDays ?? 0,
                    PaidLeaveDays = c?.PaidLeaveDays ?? 0,
                    AbsentDays = c?.AbsentDays ?? 0,
                    DailyRate = c?.DailyRate,
                    SemiMonthlyBasic = c?.SemiMonthlyBasic,
                    AbsenceDeduction = c?.AbsenceDeduction,
                    OvertimeHours = c?.OvertimeHours ?? 0,
                    OvertimePay = c?.OvertimePay,
                    OfficeAllowance = c?.OfficeAllowance,
                    MobileAllowance = c?.MobileAllowance,
                    ReimbursementTotal = c?.ReimbursementTotal,
                    DeductionTotal = c?.DeductionTotal,
                    NetPay = c?.NetPay
                });
            }
            return Ok(new { period, rows, finalized });
        }

        var staff = await _payroll.GetStaffAsync();
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
                ReimbursementTotal = slip.Computation?.ReimbursementTotal,
                DeductionTotal = slip.Computation?.DeductionTotal,
                NetPay = slip.Computation?.NetPay
            });
        }

        return Ok(new { period, rows, finalized });
    }

    /// <summary>
    /// POST /api/payroll/finalize?year=&amp;month=&amp;cutoff= — closes a cutoff:
    /// snapshots every staff payslip and locks the period permanently. HR admin only.
    /// </summary>
    [HttpPost("finalize")]
    [Authorize(Policy = "HrAdmin")]
    public async Task<IActionResult> Finalize([FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? cutoff)
    {
        var uid = CurrentUserId();
        var today = PhClock.Today;
        var period = PayrollService.ResolvePeriod(year ?? today.Year, month ?? today.Month, cutoff ?? PayrollService.DefaultCutoff(today));

        await _payroll.FinalizeAsync(period, uid);
        return Ok(new { period, finalized = true, message = "Cutoff finalized. Payslips are now locked." });
    }

    /// <summary>
    /// GET /api/payroll/export?year=&amp;month=&amp;cutoff= — bulk payroll for the
    /// chosen cutoff as a printable, styled payslip summary table (one row per
    /// staff member). HR admin only. Mirrors the summary computation and includes
    /// the incentive breakdown. Columns that are zero/blank/null for every staff
    /// member are omitted so the printed report only shows real data. Only the
    /// payroll breakdown is listed — the profile columns (Email, Department,
    /// Position, Role, Status) are not part of the summary.
    /// </summary>
    [HttpGet("export")]
    [Authorize(Policy = "HrAdmin")]
    public async Task<IActionResult> Export([FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? cutoff)
    {
        var today = PhClock.Today;
        var period = PayrollService.ResolvePeriod(year ?? today.Year, month ?? today.Month, cutoff ?? PayrollService.DefaultCutoff(today));

        // Finalized → export the frozen snapshots; otherwise compute live.
        var finalized = await _payroll.IsFinalizedAsync(period);
        var snapshotSlips = finalized ? await _payroll.GetSnapshotsAsync(period) : null;
        var staff = finalized ? new List<Profile>() : await _payroll.GetStaffAsync();

        // Unified list of payslips to export: snapshots (finalized) or live compute.
        var exportSlips = new List<PayrollPayslip>();
        if (finalized && snapshotSlips is not null)
        {
            exportSlips.AddRange(snapshotSlips);
        }
        else
        {
            foreach (var person in staff)
            {
                exportSlips.Add(await _payroll.ComputeAsync(person, period));
            }
        }

        // Hide any column whose value is zero/blank/null for every employee, so the
        // printed summary only shows the data this cutoff actually has. Employee is
        // the row label, so it is always kept regardless of its values.
        var columns = ReportColumns
            .Where(c => c.Header == "Employee" || exportSlips.Any(s => !ReportCellEmpty(c.Value(s), c)))
            .ToList();

        decimal totalNet = 0m;
        foreach (var slip in exportSlips)
        {
            totalNet += slip.Computation?.NetPay ?? 0m;
        }

        // Border values are applied by the renderer from the column's own class,
        // so we only pass labels + per-column alignment + the already-formatted text.
        var headers = columns.Select(c => c.Header).ToList();
        var numericFlags = columns.Select(c => c.IsNumeric).ToList();

        var rows = exportSlips
            .Select(slip => (IReadOnlyList<string>)columns.Select(c => c.Format(c.Value(slip))).ToList())
            .ToList();

        // Bold total row: label under Employee, summed net pay under NetPay.
        var totalRow = columns
            .Select(col => col.Header switch
            {
                "Employee" => "TOTAL NET PAY",
                "NetPay" => FormatAmount(totalNet),
                _ => ""
            })
            .ToList();

        var title = $"Payroll Cutoff {period.Start:yyyy-MM-dd} to {period.End:yyyy-MM-dd}";

        if (exportSlips.Count == 0)
        {
            headers = new List<string> { "Notice" };
            numericFlags = new List<bool> { false };
            rows = new List<IReadOnlyList<string>>
            {
                new List<string> { "No staff found for this cutoff." }
            };
            totalRow = new List<string> { "" };
        }

        var bytes = PayslipPdfRenderer.Render(title, headers, numericFlags, rows, totalRow);
        var fileName = $"payroll-{period.Year:D4}{period.Month:D2}-cutoff{period.Cutoff}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    // ---- Payslip summary report (HTML) ---------------------------------------
    //
    // One ReportColumn per report column. This is the numeric payroll breakdown
    // only — the identity/profile columns (Email, Department, Position, Role,
    // Status) are deliberately excluded; the employee name is the row label.
    // Value() returns the raw value so the "all zero / blank" column filter and
    // the rendered cell text both derive from the same source; Format() only
    // controls presentation.

    private sealed record ReportColumn(string Header, Func<PayrollPayslip, object?> Value, bool IsNumeric, Func<object?, string> Format);

    private static readonly ReportColumn[] ReportColumns =
    {
        Text("Employee", s => s.Staff.FullName),
        Num("BasicSalary", s => s.Staff.BasicSalary, "0.00"),
        Num("DailyRate", s => s.Computation?.DailyRate, "0.00"),
        Num("Workdays", s => s.Computation?.Workdays, "0"),
        Num("DaysWorked", s => s.Computation?.WorkedDays, "0"),
        Num("PaidLeaveDays", s => s.Computation?.PaidLeaveDays, "0"),
        Num("AbsenceDeduction", s => s.Computation?.AbsenceDeduction, "0.00"),
        Num("OvertimeHours", s => s.Computation?.OvertimeHours, "0.##"),
        Num("OvertimePay", s => s.Computation?.OvertimePay, "0.00"),
        Num("OfficeIncentive", s => s.Computation?.OfficeAllowance, "0.00"),
        Num("MobileIncentive", s => s.Computation?.MobileAllowance, "0.00"),
        Num("Reimbursements", s => s.Computation?.ReimbursementTotal, "0.00"),
        Num("CashAdvances/deductions", s => s.Computation?.DeductionTotal, "0.00"),
        Num("SundayDays", s => s.Computation?.SundayDays, "0"),
        Num("SundayPay", s => s.Computation?.SundayPay, "0.00"),
        Num("NetPay", s => s.Computation?.NetPay, "0.00"),
    };

    private static ReportColumn Text(string header, Func<PayrollPayslip, object?> value) =>
        new(header, value, false, v => v?.ToString() ?? "");

    private static ReportColumn Num(string header, Func<PayrollPayslip, object?> value, string format) =>
        new(header, value, true, v => v is null ? "" : Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(format, CultureInfo.InvariantCulture));

    /// <summary>
    /// True when a cell carries no information: null, blank text, or numeric zero.
    /// A column where every employee is "empty" is dropped from the report.
    /// </summary>
    private static bool ReportCellEmpty(object? value, ReportColumn column)
    {
        if (value is null)
        {
            return true;
        }
        if (value is string s)
        {
            return string.IsNullOrWhiteSpace(s);
        }
        if (value is bool b)
        {
            return !b;
        }
        // Whole-day/hour counts (int, long) and money/hours (decimal, double).
        return column.IsNumeric && decimal.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var d) && d == 0m;
    }

    private static string FormatAmount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

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

        var finalized = await _payroll.IsFinalizedAsync(period);
        var attSlips = new List<PayrollPayslip>();
        if (finalized)
        {
            attSlips.AddRange(await _payroll.GetSnapshotsAsync(period));
        }
        else
        {
            foreach (var person in await _payroll.GetStaffAsync())
            {
                attSlips.Add(await _payroll.ComputeAsync(person, period));
            }
        }

        var sb = new StringBuilder();
        sb.Append("Pinoy Ride — Attendance ").Append(period.Cutoff == 1 ? "Cutoff 1 (1–15)" : "Cutoff 2 (16–end of month)")
          .Append(' ').Append(period.Start.ToString("yyyy-MM-dd")).Append(" to ").Append(period.End.ToString("yyyy-MM-dd"))
          .AppendLine();
        sb.AppendLine();
        sb.AppendLine("Employee,Email,Department,WorkDate,Weekday,Status,TimeIn,TimeOut,Hours,OvertimeHours,Setup");

        foreach (var slip in attSlips)
        {
            var person = slip.Staff;
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

        // Finalized period → return the frozen snapshot (immutable), if one exists.
        var snapshot = await _payroll.GetSnapshotAsync(period, target);
        if (snapshot is not null)
        {
            return Ok(snapshot);
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