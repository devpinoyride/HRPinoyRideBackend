using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Controllers;

[ApiController]
[Route("api/clock")]
[Authorize]
public class ClockController : ControllerBase
{
    private readonly Db _db;
    private readonly AuditService _audit;

    public ClockController(Db db, AuditService audit)
    {
        _db = db;
        _audit = audit;
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

    /// <summary>POST /api/clock/in — upsert today's entry (insert when missing).</summary>
    [HttpPost("in")]
    public async Task<IActionResult> ClockIn([FromBody] ClockInRequest? request)
    {
        var uid = CurrentUserId();
        var today = PhClock.Today;

        var setup = (request?.WorkSetup ?? "office").Trim().ToLowerInvariant();
        if (setup is not ("office" or "wfh"))
        {
            return StatusCode(422, new { error = "workSetup must be 'office' or 'wfh'." });
        }

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var existing = await con.QuerySingleOrDefaultAsync<TimeEntry>(
            """
            select id, user_id, work_date, time_in, time_out,
                   source::text as source, status::text as status, work_setup::text as work_setup
            from time_entries
            where user_id = @Uid::uuid and work_date = @Date
            """,
            new { Uid = uid, Date = today }, tx);

        if (existing is not null)
        {
            return Ok(existing);
        }

        var row = await con.QuerySingleAsync<TimeEntry>(
            """
            insert into time_entries (user_id, work_date, time_in, source, status, work_setup)
            values (@Uid::uuid, @Date, @Now, 'self_logged', 'confirmed', @Setup::work_setup)
            returning id, user_id, work_date, time_in, time_out,
                      source::text as source, status::text as status, work_setup::text as work_setup,
                      created_at, updated_at
            """,
            new { Uid = uid, Date = today, Now = DateTime.UtcNow, Setup = setup }, tx);

        await _audit.AddAsync(con, tx, uid, "clock_in", "time_entries", row.Id.ToString(),
            new { user_id = uid, work_date = today, time_in = row.TimeIn });

        tx.Commit();
        return StatusCode(201, row);
    }

    /// <summary>POST /api/clock/out — set time_out on today's open entry.</summary>
    [HttpPost("out")]
    public async Task<IActionResult> ClockOut()
    {
        var uid = CurrentUserId();
        var today = PhClock.Today;

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var row = await con.QuerySingleOrDefaultAsync<TimeEntry>(
            """
            select id, user_id, work_date, time_in, time_out,
                   source::text as source, status::text as status, work_setup::text as work_setup
            from time_entries
            where user_id = @Uid::uuid and work_date = @Date
            """,
            new { Uid = uid, Date = today }, tx);

        if (row is null)
        {
            return StatusCode(409, new { error = "You have not clocked in today." });
        }

        if (row.TimeOut is not null)
        {
            return StatusCode(409, new { error = "You have already clocked out today." });
        }

        row = await con.QuerySingleAsync<TimeEntry>(
            """
            update time_entries
            set time_out = @Now, updated_at = now()
            where id = @Id
            returning id, user_id, work_date, time_in, time_out,
                      source::text as source, status::text as status, work_setup::text as work_setup,
                      created_at, updated_at
            """,
            new { Now = DateTime.UtcNow, Id = row.Id }, tx);

        await _audit.AddAsync(con, tx, uid, "clock_out", "time_entries", row.Id.ToString(),
            new { time_out = row.TimeOut });

        tx.Commit();
        return Ok(row);
    }

    /// <summary>POST /api/clock/reset-today — dev-only: delete today's entry so the user can re-clock.</summary>
    [HttpPost("reset-today")]
    public async Task<IActionResult> ResetToday()
    {
        var uid = CurrentUserId();
        var today = PhClock.Today;

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var existing = await con.QuerySingleOrDefaultAsync<TimeEntry>(
            """
            select id, user_id, work_date, time_in, time_out,
                   source::text as source, status::text as status, work_setup::text as work_setup
            from time_entries
            where user_id = @Uid::uuid and work_date = @Date
            """,
            new { Uid = uid, Date = today }, tx);

        if (existing is null)
        {
            return Ok(new { message = "No entry for today." });
        }

        await con.ExecuteAsync(
            "delete from time_entries where id = @Id",
            new { Id = existing.Id }, tx);

        await _audit.AddAsync(con, tx, uid, "reset_today", "time_entries", existing.Id.ToString(),
            new { work_date = today });

        tx.Commit();
        return Ok(new { message = "Today's entry has been reset." });
    }

    /// <summary>GET /api/clock/today — today's entry plus the current week for this user.</summary>
    [HttpGet("today")]
    public async Task<IActionResult> Today()
    {
        var uid = CurrentUserId();
        var today = PhClock.Today;
        var weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7); // Monday of this week

        using var con = _db.Open();
        var entries = (await con.QueryAsync<TimeEntry>(
            """
            select * from time_entries
            where user_id = @Uid::uuid and work_date between @From and @To
            order by work_date asc
            """,
            new { Uid = uid, From = weekStart, To = weekStart.AddDays(6) })).AsList();

        return Ok(new
        {
            today = entries.FirstOrDefault(e => e.WorkDate == today),
            week = entries
        });
    }
}