using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Controllers;

[ApiController]
[Route("api/approvals")]
[Authorize(Policy = "ApproverOrAbove")]
public class ApprovalsController : ControllerBase
{
    private readonly Db _db;
    private readonly AuditService _audit;

    public ApprovalsController(Db db, AuditService audit)
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

    private bool IsHrAdmin() => User.FindFirst("role")?.Value == "hr_admin";

    /// <summary>
    /// GET /api/approvals — pending requests assigned to this approver,
    /// or every pending request when the caller is an HR admin.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var uid = CurrentUserId();
        using var con = _db.Open();

        IEnumerable<TimekeepingRequest> rows;
        if (IsHrAdmin())
        {
            rows = await con.QueryAsync<TimekeepingRequest>(
                """
                select r.*, p.full_name
                from timekeeping_requests r
                join profiles p on p.id = r.user_id
                where r.status = 'pending'
                order by r.created_at asc
                """);
        }
        else
        {
            rows = await con.QueryAsync<TimekeepingRequest>(
                """
                select r.*, p.full_name
                from timekeeping_requests r
                join profiles p on p.id = r.user_id
                where r.status = 'pending' and r.approver_id = @Uid::uuid
                order by r.created_at asc
                """,
                new { Uid = uid });
        }

        return Ok(rows);
    }
/// <summary>
    /// POST /api/approvals/{id}/approve — resolves the request and writes an
    /// adjusted time entry for the staff member using the requested times.
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, [FromBody] ResolveRequestRequest? request)
    {
        var uid = CurrentUserId();
        var notes = request?.Notes?.Trim();

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var req = await con.QuerySingleOrDefaultAsync<TimekeepingRequest>(
            "select * from timekeeping_requests where id = @Id",
            new { Id = id }, tx);
        if (req is null)
        {
            throw new ApiException(404, "Request not found.");
        }
        if (req.Status != "pending")
        {
            return StatusCode(409, new { error = "This request has already been resolved." });
        }
        if (!IsHrAdmin() && req.ApproverId != uid)
        {
            return StatusCode(403, new { error = "You are not the approver assigned to this request." });
        }

        var updated = await con.QuerySingleAsync<TimekeepingRequest>(
            """
            update timekeeping_requests
            set status = 'approved', approver_notes = @Notes, resolved_at = now()
            where id = @Id
            returning *
            """,
            new { Notes = notes, Id = id }, tx);

        // Only adjustment / overtime requests write an actual time entry (the
        // requested clock in/out become the worked shift). A LEAVE (or 'other')
        // must NOT create a worked entry — payroll recognizes approved leave via
        // the leave request itself (status='approved', request_type='leave') and
        // pays it as paid leave. Writing a time entry here would make the day
        // count as "present" instead of "paid_leave", so leave is skipped.
        var writesTimeEntry = string.Equals(req.RequestType, "adjustment", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(req.RequestType, "overtime", StringComparison.OrdinalIgnoreCase);

        if (writesTimeEntry)
        {
            var entry = await con.QuerySingleAsync<TimeEntry>(
                """
                insert into time_entries (user_id, work_date, time_in, time_out, source, status)
                values (@UserId::uuid, @WorkDate, @In, @Out, 'adjusted', 'confirmed')
                on conflict (user_id, work_date)
                do update set
                    time_in = excluded.time_in,
                    time_out = excluded.time_out,
                    source = 'adjusted',
                    status = 'confirmed',
                    updated_at = now()
                returning id, user_id, work_date, time_in, time_out,
                          source::text as source, status::text as status, work_setup::text as work_setup,
                          created_at, updated_at
                """,
                new
                {
                    UserId = req.UserId,
                    WorkDate = req.WorkDate,
                    In = req.RequestedTimeIn.HasValue ? PhClock.Combine(req.WorkDate, req.RequestedTimeIn.Value) : (DateTime?)null,
                    Out = req.RequestedTimeOut.HasValue ? PhClock.Combine(req.WorkDate, req.RequestedTimeOut.Value) : (DateTime?)null
                }, tx);

            await _audit.AddAsync(con, tx, uid, "adjust_time_entry", "time_entries", entry.Id.ToString(),
                new
                {
                    user_id = req.UserId,
                    work_date = req.WorkDate,
                    time_in = entry.TimeIn,
                    time_out = entry.TimeOut
                });
        }

        await _audit.AddAsync(con, tx, uid, "approve_request", "timekeeping_requests", updated.Id.ToString(),
            new
            {
                user_id = req.UserId,
                work_date = req.WorkDate,
                request_type = req.RequestType,
                requested_time_in = req.RequestedTimeIn,
                requested_time_out = req.RequestedTimeOut,
                notes
            });

        tx.Commit();
        return Ok(updated);
    }

    /// <summary>POST /api/approvals/{id}/reject — rejects with a required note.</summary>
    [HttpPost("{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, [FromBody] ResolveRequestRequest? request)
    {
        var uid = CurrentUserId();
        var notes = request?.Notes?.Trim();

        if (string.IsNullOrWhiteSpace(notes))
        {
            return StatusCode(422, new { error = "A non-empty note is required to reject a request." });
        }

        using var con = _db.Open();

        var req = await con.QuerySingleOrDefaultAsync<TimekeepingRequest>(
            "select * from timekeeping_requests where id = @Id",
            new { Id = id });
        if (req is null)
        {
            throw new ApiException(404, "Request not found.");
        }
        if (req.Status != "pending")
        {
            return StatusCode(409, new { error = "This request has already been resolved." });
        }
        if (!IsHrAdmin() && req.ApproverId != uid)
        {
            return StatusCode(403, new { error = "You are not the approver assigned to this request." });
        }

        using var tx = con.BeginTransaction();
        var updated = await con.QuerySingleAsync<TimekeepingRequest>(
            """
            update timekeeping_requests
            set status = 'rejected', approver_notes = @Notes, resolved_at = now()
            where id = @Id
            returning *
            """,
            new { Notes = notes, Id = id }, tx);

        await _audit.AddAsync(con, tx, uid, "reject_request", "timekeeping_requests", updated.Id.ToString(),
            new { user_id = req.UserId, work_date = req.WorkDate, notes });

        tx.Commit();
        return Ok(updated);
    }
}