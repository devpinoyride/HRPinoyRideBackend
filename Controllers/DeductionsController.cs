using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Controllers;

/// <summary>
/// Deductions / cash advances: staff file a request with a note and an amount;
/// an approver (or HR admin) approves it and the amount is SUBTRACTED from the
/// staff member's payslip for the cutoff in which it was approved.
/// </summary>
[ApiController]
[Route("api/deductions")]
[Authorize]
public class DeductionsController : ControllerBase
{
    private readonly Db _db;
    private readonly AuditService _audit;

    public DeductionsController(Db db, AuditService audit)
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
    /// POST /api/deductions — file a deduction / cash advance request
    /// (status pending). The amount is subtracted from the payslip once approved.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDeductionRequest? request)
    {
        if (request is null)
        {
            return StatusCode(422, new { error = "A deduction body is required." });
        }
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            return StatusCode(422, new { error = "note is required." });
        }
        if (request.Amount is null || request.Amount <= 0)
        {
            return StatusCode(422, new { error = "amount must be greater than zero." });
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

        var row = await con.QuerySingleAsync<Deduction>(
            """
            insert into deductions (user_id, note, amount, approver_id, status)
            values (@Uid::uuid, @Note, @Amount, @ApproverId::uuid, 'pending')
            returning *
            """,
            new
            {
                Uid = uid,
                Note = request.Note.Trim(),
                Amount = request.Amount,
                ApproverId = me.ApproverId
            }, tx);

        await _audit.AddAsync(con, tx, uid, "create_deduction", "deductions", row.Id.ToString(),
            new
            {
                user_id = uid,
                note = request.Note.Trim(),
                amount = request.Amount
            });

        tx.Commit();
        return StatusCode(201, row);
    }

    /// <summary>GET /api/deductions/mine — the current user's requests, newest first.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var uid = CurrentUserId();
        using var con = _db.Open();

        var rows = await con.QueryAsync<Deduction>(
            """
            select * from deductions
            where user_id = @Uid::uuid
            order by created_at desc
            """,
            new { Uid = uid });

        return Ok(rows);
    }

    /// <summary>
    /// GET /api/deductions/pending — pending deduction requests assigned to this
    /// approver, or every pending one when the caller is an HR admin.
    /// </summary>
    [HttpGet("pending")]
    [Authorize(Policy = "ApproverOrAbove")]
    public async Task<IActionResult> Pending()
    {
        var uid = CurrentUserId();
        using var con = _db.Open();

        IEnumerable<Deduction> rows;
        if (IsHrAdmin())
        {
            rows = await con.QueryAsync<Deduction>(
                """
                select d.*, p.full_name
                from deductions d
                join profiles p on p.id = d.user_id
                where d.status = 'pending'
                order by d.created_at asc
                """);
        }
        else
        {
            rows = await con.QueryAsync<Deduction>(
                """
                select d.*, p.full_name
                from deductions d
                join profiles p on p.id = d.user_id
                where d.status = 'pending' and d.approver_id = @Uid::uuid
                order by d.created_at asc
                """,
                new { Uid = uid });
        }

        return Ok(rows);
    }

    /// <summary>POST /api/deductions/{id}/approve — approves the request; the amount is then subtracted from the staff member's payslip.</summary>
    [HttpPost("{id:long}/approve")]
    [Authorize(Policy = "ApproverOrAbove")]
    public async Task<IActionResult> Approve(long id, [FromBody] ResolveRequestRequest? request)
    {
        var uid = CurrentUserId();
        var notes = request?.Notes?.Trim();

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var ded = await con.QuerySingleOrDefaultAsync<Deduction>(
            "select * from deductions where id = @Id",
            new { Id = id }, tx);
        if (ded is null)
        {
            throw new ApiException(404, "Deduction request not found.");
        }
        if (ded.Status != "pending")
        {
            return StatusCode(409, new { error = "This request has already been resolved." });
        }
        if (!IsHrAdmin() && ded.ApproverId != uid)
        {
            return StatusCode(403, new { error = "You are not the approver assigned to this request." });
        }

        var updated = await con.QuerySingleAsync<Deduction>(
            """
            update deductions
            set status = 'approved', approver_notes = @Notes, resolved_at = now()
            where id = @Id
            returning *
            """,
            new { Notes = notes, Id = id }, tx);

        await _audit.AddAsync(con, tx, uid, "approve_deduction", "deductions", updated.Id.ToString(),
            new
            {
                user_id = ded.UserId,
                note = ded.Note,
                amount = ded.Amount,
                notes
            });

        tx.Commit();
        return Ok(updated);
    }

    /// <summary>POST /api/deductions/{id}/reject — rejects with a required note.</summary>
    [HttpPost("{id:long}/reject")]
    [Authorize(Policy = "ApproverOrAbove")]
    public async Task<IActionResult> Reject(long id, [FromBody] ResolveRequestRequest? request)
    {
        var uid = CurrentUserId();
        var notes = request?.Notes?.Trim();

        if (string.IsNullOrWhiteSpace(notes))
        {
            return StatusCode(422, new { error = "A non-empty note is required to reject a request." });
        }

        using var con = _db.Open();

        var ded = await con.QuerySingleOrDefaultAsync<Deduction>(
            "select * from deductions where id = @Id",
            new { Id = id });
        if (ded is null)
        {
            throw new ApiException(404, "Deduction request not found.");
        }
        if (ded.Status != "pending")
        {
            return StatusCode(409, new { error = "This request has already been resolved." });
        }
        if (!IsHrAdmin() && ded.ApproverId != uid)
        {
            return StatusCode(403, new { error = "You are not the approver assigned to this request." });
        }

        using var tx = con.BeginTransaction();
        var updated = await con.QuerySingleAsync<Deduction>(
            """
            update deductions
            set status = 'rejected', approver_notes = @Notes, resolved_at = now()
            where id = @Id
            returning *
            """,
            new { Notes = notes, Id = id }, tx);

        await _audit.AddAsync(con, tx, uid, "reject_deduction", "deductions", updated.Id.ToString(),
            new { user_id = ded.UserId, note = ded.Note, amount = ded.Amount, notes });

        tx.Commit();
        return Ok(updated);
    }
}