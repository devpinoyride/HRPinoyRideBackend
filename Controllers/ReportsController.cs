using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize(Policy = "ApproverOrAbove")]
public class ReportsController : ControllerBase
{
    private readonly Db _db;

    public ReportsController(Db db)
    {
        _db = db;
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
    /// GET /api/reports?from=&amp;to=&amp;staffId= — joined time_entries +
    /// timekeeping_requests summary. Approvers only see their assigned staff.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Summary([FromQuery] string? from, [FromQuery] string? to, [FromQuery] Guid? staffId)
    {
        var fromDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", out var f) ? f : PhClock.Today.AddDays(-30);
        var toDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", out var t) ? t : PhClock.Today;
        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        using var con = _db.Open();
        var entries = (await QueryEntriesAsync(con, fromDate, toDate, staffId)).ToList();
        var requests = (await QueryRequestsAsync(con, fromDate, toDate, staffId)).ToList();

        // Expose Philippines wall-clock times and computed hours for display.
        foreach (var entry in entries)
        {
            entry.TimeIn = PhClock.ToLocal(entry.TimeIn);
            entry.TimeOut = PhClock.ToLocal(entry.TimeOut);
            entry.Hours = entry.TimeIn.HasValue && entry.TimeOut.HasValue
                ? Math.Round((entry.TimeOut.Value - entry.TimeIn.Value).TotalHours, 2)
                : null;
        }

        return Ok(new
        {
            from = fromDate,
            to = toDate,
            entries,
            requests,
            summary = new
            {
                total_entries = entries.Count,
                total_requests = requests.Count,
                total_hours = entries.Where(e => e.Hours.HasValue).Sum(e => e.Hours!.Value)
            }
        });
    }

    private async Task<IEnumerable<TimeEntry>> QueryEntriesAsync(NpgsqlConnection con, DateOnly from, DateOnly to, Guid? staffId)
    {
        var sql = """
            select te.id, te.user_id, te.work_date, te.time_in, te.time_out, te.source, te.status, te.work_setup::text as work_setup,
                   p.full_name, p.email
            from time_entries te
            join profiles p on p.id = te.user_id
            where te.work_date between @From and @To
              and p.role <> 'hr_admin'
            """;
        var parameters = new DynamicParameters();
        parameters.Add("From", from);
        parameters.Add("To", to);

        if (staffId.HasValue)
        {
            sql += " and te.user_id = @StaffId::uuid";
            parameters.Add("staffId", staffId.Value);
        }
        if (!IsHrAdmin())
        {
            sql += " and p.approver_id = @Uid::uuid";
            parameters.Add("uid", CurrentUserId());
        }

        sql += " order by te.work_date asc, p.full_name asc";
        return await con.QueryAsync<TimeEntry>(sql, parameters);
    }

    private async Task<IEnumerable<TimekeepingRequest>> QueryRequestsAsync(NpgsqlConnection con, DateOnly from, DateOnly to, Guid? staffId)
    {
        var sql = """
            select r.id, r.user_id, r.work_date, r.requested_time_in, r.requested_time_out,
                   r.request_type, r.reason, r.approver_id, r.status, r.approver_notes,
                   r.resolved_at, r.created_at, p.full_name, p.email
            from timekeeping_requests r
            join profiles p on p.id = r.user_id
            where r.work_date between @From and @To
              and p.role <> 'hr_admin'
            """;
        var parameters = new DynamicParameters();
        parameters.Add("From", from);
        parameters.Add("To", to);

        if (staffId.HasValue)
        {
            sql += " and r.user_id = @StaffId::uuid";
            parameters.Add("staffId", staffId.Value);
        }
        if (!IsHrAdmin())
        {
            sql += " and p.approver_id = @Uid::uuid";
            parameters.Add("uid", CurrentUserId());
        }

        sql += " order by r.work_date asc, p.full_name asc";
        return await con.QueryAsync<TimekeepingRequest>(sql, parameters);
    }

    /// <summary>
    /// GET /api/reports/export?from=&amp;to=&amp;staffId= — same filtered data as a CSV file.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? from, [FromQuery] string? to, [FromQuery] Guid? staffId)
    {
        var fromDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", out var f) ? f : PhClock.Today.AddDays(-30);
        var toDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", out var t) ? t : PhClock.Today;
        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        using var con = _db.Open();
        var entries = (await QueryEntriesAsync(con, fromDate, toDate, staffId)).ToList();
        var requests = (await QueryRequestsAsync(con, fromDate, toDate, staffId)).ToList();

        foreach (var entry in entries)
        {
            entry.TimeIn = PhClock.ToLocal(entry.TimeIn);
            entry.TimeOut = PhClock.ToLocal(entry.TimeOut);
            entry.Hours = entry.TimeIn.HasValue && entry.TimeOut.HasValue
                ? Math.Round((entry.TimeOut.Value - entry.TimeIn.Value).TotalHours, 2)
                : null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("SECTION,WorkDate,Employee,Email,TimeIn,TimeOut,Source,Status,Hours,RequestType,RequestedTimeIn,RequestedTimeOut,Reason,ApproverNotes,ResolvedAt,Setup");

        foreach (var e in entries)
        {
            sb.Append("ENTRY,")
              .Append(Csv(e.WorkDate.ToString("yyyy-MM-dd"))).Append(',')
              .Append(Csv(e.FullName)).Append(',')
              .Append(Csv(e.Email)).Append(',')
              .Append(Csv(e.TimeIn?.ToString("yyyy-MM-dd HH:mm"))).Append(',')
              .Append(Csv(e.TimeOut?.ToString("yyyy-MM-dd HH:mm"))).Append(',')
              .Append(Csv(e.Source)).Append(',')
              .Append(Csv(e.Status)).Append(',')
              .Append(Csv(e.Hours?.ToString("0.00")))
              .Append(',')
              .Append(Csv(e.WorkSetup))
              .AppendLine();
        }

        foreach (var r in requests)
        {
            sb.Append("REQUEST,")
              .Append(Csv(r.WorkDate.ToString("yyyy-MM-dd"))).Append(',')
              .Append(Csv(r.FullName)).Append(',')
              .Append(Csv(r.Email)).Append(',')
              .Append(',').Append(',')
              .Append(',')
              .Append(',').Append(',')
              .Append(Csv(r.RequestType)).Append(',')
              .Append(Csv(r.RequestedTimeIn?.ToString("HH:mm"))).Append(',')
              .Append(Csv(r.RequestedTimeOut?.ToString("HH:mm"))).Append(',')
              .Append(Csv(r.Reason)).Append(',')
              .Append(Csv(r.ApproverNotes)).Append(',')
              .Append(Csv(r.ResolvedAt?.ToString("yyyy-MM-dd HH:mm")))
              .Append(',')
              .AppendLine();
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"reports-{PhClock.Today:yyyyMMdd}.csv");
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
}