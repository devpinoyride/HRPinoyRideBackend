using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Controllers;

[ApiController]
[Route("api/requests")]
[Authorize]
public class RequestsController : ControllerBase
{
    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "adjustment", "leave", "overtime", "other" };

    private static readonly HashSet<string> AllowedLeaveDurations =
        new(StringComparer.OrdinalIgnoreCase) { "whole", "half_am", "half_pm" };

    /// <summary>Minimum advance notice (days) required to file a leave.</summary>
    private const int LeaveAdvanceDays = 3;

    private readonly Db _db;
    private readonly AuditService _audit;

    public RequestsController(Db db, AuditService audit)
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

    /// <summary>POST /api/requests — create a timekeeping request (pending).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequestRequest? request)
    {
        if (request is null)
        {
            return StatusCode(422, new { error = "A request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return StatusCode(422, new { error = "reason is required." });
        }

        if (!DateOnly.TryParseExact(request.WorkDate, "yyyy-MM-dd", out var workDate))
        {
            return StatusCode(422, new { error = "work_date must be a date in YYYY-MM-DD format." });
        }

        if (!TimeOnly.TryParse(request.RequestedTimeIn, out var timeIn))
        {
            return StatusCode(422, new { error = "requested_time_in must be a time in HH:mm format." });
        }

        if (!TimeOnly.TryParse(request.RequestedTimeOut, out var timeOut))
        {
            return StatusCode(422, new { error = "requested_time_out must be a time in HH:mm format." });
        }

        var type = (request.RequestType ?? "").Trim();
        if (!AllowedTypes.Contains(type))
        {
            return StatusCode(422, new { error = $"request_type must be one of: {string.Join(", ", AllowedTypes)}." });
        }

        // Date rules are type-specific:
        //   leave     → a FUTURE date filed at least 3 days in advance
        //   others    → a past or current date (correcting what already happened)
        var isLeave = string.Equals(type, "leave", StringComparison.OrdinalIgnoreCase);
        string? leaveDuration = null;
        if (isLeave)
        {
            var earliest = PhClock.Today.AddDays(LeaveAdvanceDays);
            if (workDate < earliest)
            {
                return StatusCode(422, new { error = $"Leave must be filed at least {LeaveAdvanceDays} days in advance. The earliest date you can select is {earliest:yyyy-MM-dd}." });
            }

            leaveDuration = (request.LeaveDuration ?? "whole").Trim().ToLowerInvariant();
            if (!AllowedLeaveDurations.Contains(leaveDuration))
            {
                return StatusCode(422, new { error = $"leaveDuration must be one of: {string.Join(", ", AllowedLeaveDurations)}." });
            }
        }
        else if (workDate > PhClock.Today)
        {
            return StatusCode(422, new { error = "work_date cannot be in the future for this request type." });
        }

        var uid = CurrentUserId();
        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var me = await con.QuerySingleOrDefaultAsync<Profile>(
            "select id, approver_id from profiles where id = @Uid::uuid",
            new { Uid = uid }, tx);
        if (me is null)
        {
            throw new ApiException(401, "Your profile could not be found.");
        }

        var row = await con.QuerySingleAsync<TimekeepingRequest>(
            """
            insert into timekeeping_requests
                (user_id, work_date, requested_time_in, requested_time_out, request_type, reason, leave_duration, approver_id, status)
            values
                (@Uid::uuid, @WorkDate, @In::time, @Out::time, @Type::request_type, @Reason, @LeaveDuration, @ApproverId::uuid, 'pending')
            returning *
            """,
            new
            {
                Uid = uid,
                WorkDate = workDate,
                In = timeIn,
                Out = timeOut,
                Type = type,
                Reason = request.Reason.Trim(),
                LeaveDuration = leaveDuration,
                ApproverId = me.ApproverId
            }, tx);

        await _audit.AddAsync(con, tx, uid, "create_request", "timekeeping_requests", row.Id.ToString(),
            new
            {
                user_id = uid,
                work_date = workDate,
                requested_time_in = timeIn,
                requested_time_out = timeOut,
                request_type = type,
                reason = request.Reason.Trim()
            });

        tx.Commit();
        return StatusCode(201, row);
    }

    /// <summary>GET /api/requests/mine — the current user's requests, newest first.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var uid = CurrentUserId();
        using var con = _db.Open();

        var rows = await con.QueryAsync<TimekeepingRequest>(
            """
            select * from timekeeping_requests
            where user_id = @Uid::uuid
            order by created_at desc
            """,
            new { Uid = uid });

        return Ok(rows);
    }
}