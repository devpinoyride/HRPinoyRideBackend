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

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\" />");
        sb.Append("<title>Payroll Cutoff ").Append(Html(period.Start.ToString("yyyy-MM-dd")))
          .Append(" to ").Append(Html(period.End.ToString("yyyy-MM-dd"))).AppendLine("</title>");
        sb.Append("<style>").AppendLine(ReportStyles).AppendLine("</style></head><body>");

        // Bordered, centered title bar documenting the cutoff period.
        sb.Append("<div class=\"title-bar\">Payroll Cutoff ")
          .Append(Html(period.Start.ToString("yyyy-MM-dd")))
          .Append(" to ")
          .Append(Html(period.End.ToString("yyyy-MM-dd")))
          .Append("</div>");

        if (exportSlips.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">No staff found for this cutoff.</p></body></html>");
            var emptyBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var emptyName = $"payroll-{period.Year:D4}{period.Month:D2}-cutoff{period.Cutoff}.html";
            return File(emptyBytes, "text/html; charset=utf-8", emptyName);
        }

        sb.AppendLine("<table class=\"payslip-summary\">");
        sb.Append("<thead><tr>");
        foreach (var col in columns)
        {
            sb.Append("<th").Append(col.IsNumeric ? " class=\"num\"" : "").Append('>')
              .Append(Html(col.Header)).Append("</th>");
        }
        sb.AppendLine("</tr></thead>");

        sb.AppendLine("<tbody>");
        foreach (var slip in exportSlips)
        {
            sb.Append("<tr>");
            foreach (var col in columns)
            {
                sb.Append("<td").Append(col.CssClass).Append('>')
                  .Append(Html(col.Format(col.Value(slip))))
                  .Append("</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody>");

        // Trailing total row for quick reconciliation, with a divider line above it.
        sb.AppendLine("<tfoot><tr>");
        foreach (var col in columns)
        {
            sb.Append("<td");
            if (col.Header == "NetPay")
            {
                sb.Append(" class=\"num total-value\"");
            }
            else if (col.Header == "Employee")
            {
                sb.Append(" class=\"total-label\"");
            }
            sb.Append('>')
              .Append(col.Header switch
              {
                  "Employee" => Html("TOTAL NET PAY"),
                  "NetPay" => Html(FormatAmount(totalNet)),
                  _ => ""
              })
              .Append("</td>");
        }
        sb.AppendLine("</tr></tfoot>");

        sb.AppendLine("</table></body></html>");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"payroll-{period.Year:D4}{period.Month:D2}-cutoff{period.Cutoff}.html";
        return File(bytes, "text/html; charset=utf-8", fileName);
    }

    // ---- Payslip summary report (HTML) ---------------------------------------
    //
    // One ReportColumn per report column. This is the numeric payroll breakdown
    // only — the identity/profile columns (Email, Department, Position, Role,
    // Status) are deliberately excluded; the employee name is the row label.
    // Value() returns the raw value so the "all zero / blank" column filter and
    // the rendered cell text both derive from the same source; Format() only
    // controls presentation.

    private sealed record ReportColumn(string Header, Func<PayrollPayslip, object?> Value, bool IsNumeric, Func<object?, string> Format)
    {
        /// <summary>Cell class: numeric columns right-align, the employee name stands out.</summary>
        public string CssClass => Header switch
        {
            "Employee" => " class=\"employee\"",
            _ when IsNumeric => " class=\"num\"",
            _ => ""
        };
    }

    private static readonly ReportColumn[] ReportColumns =
    {
        Text("Employee", s => s.Staff.FullName),
        Text("SalaryMode", s => s.Computation?.SalaryMode ?? s.Staff.SalaryMode ?? "basic"),
        Num("BasicSalary", s => s.Staff.BasicSalary, "0.00"),
        Num("DailyRate", s => s.Computation?.DailyRate, "0.00"),
        Num("Workdays", s => s.Computation?.Workdays, "0"),
        Num("DaysWorked", s => s.Computation?.WorkedDays, "0"),
        Num("PaidLeaveDays", s => s.Computation?.PaidLeaveDays, "0"),
        Num("AbsentDays", s => s.Computation?.AbsentDays, "0"),
        Num("SemiMonthlyBasic", s => s.Computation?.SemiMonthlyBasic, "0.00"),
        Num("AbsenceDeduction", s => s.Computation?.AbsenceDeduction, "0.00"),
        Num("OvertimeHours", s => s.Computation?.OvertimeHours, "0.##"),
        Num("OvertimePay", s => s.Computation?.OvertimePay, "0.00"),
        Num("OfficeIncentive", s => s.Computation?.OfficeAllowance, "0.00"),
        Num("MobileIncentive", s => s.Computation?.MobileAllowance, "0.00"),
        Num("Reimbursements", s => s.Computation?.ReimbursementTotal, "0.00"),
        Num("CashAdvances", s => s.Computation?.DeductionTotal, "0.00"),
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

    /// <summary>Escapes a value for safe interpolation into HTML text/attributes.</summary>
    private static string Html(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private static string FormatAmount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // Self-contained print styles: bordered title bar, bold shaded header, zebra
    // rows, dark-blue left-aligned employee names, right-aligned plain numbers,
    // and a divider above the total row.
    private const string ReportStyles = @"
        * { box-sizing: border-box; }
        body {
            font-family: 'Segoe UI', Tahoma, Verdana, sans-serif;
            font-size: 11px;
            color: #1f2933;
            margin: 0;
            padding: 16px;
            background: #fff;
        }
        .title-bar {
            border: 2px solid #1f3a5f;
            border-radius: 4px;
            background: #e8eef7;
            color: #1f3a5f;
            font-size: 15px;
            font-weight: 700;
            text-align: center;
            letter-spacing: .3px;
            padding: 10px 12px;
            margin-bottom: 12px;
        }
        table.payslip-summary {
            border: 2px solid #1f3a5f;
            border-collapse: collapse;
            width: 100%;
        }
        table.payslip-summary th,
        table.payslip-summary td {
            border: 1px solid #9fb3c8;
            padding: 5px 7px;
            white-space: nowrap;
            vertical-align: middle;
        }
        table.payslip-summary thead th {
            background: #dce6f2;
            color: #1f3a5f;
            font-weight: 700;
            text-align: left;
            border-bottom: 2px solid #1f3a5f;
        }
        table.payslip-summary thead th.num { text-align: right; }
        table.payslip-summary td.employee {
            color: #14396b;
            font-weight: 600;
            text-align: left;
        }
        table.payslip-summary td.num { text-align: right; }
        table.payslip-summary tbody tr:nth-child(even) { background: #eef7ee; }
        table.payslip-summary tbody tr:nth-child(odd) { background: #fff; }
        table.payslip-summary tfoot td {
            font-weight: 700;
            background: #e8eef7;
            border-top: 2px solid #1f3a5f;
        }
        table.payslip-summary tfoot td.total-label { text-align: left; color: #1f3a5f; }
        table.payslip-summary tfoot td.total-value { text-align: right; color: #1f3a5f; }
        p.empty { padding: 12px; text-align: center; color: #52606d; }
        @media print {
            @page { size: landscape; margin: 8mm; }
            body { padding: 0; }
            table.payslip-summary thead { display: table-header-group; }
            table.payslip-summary tfoot { display: table-footer-group; }
            table.payslip-summary tr { page-break-inside: avoid; }
        }
    ";

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