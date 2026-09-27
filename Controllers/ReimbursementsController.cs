using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Controllers;

/// <summary>
/// Reimbursements / additional incentives: staff file a request with a note and
/// an amount; an approver (or HR admin) approves it and the amount is added to
/// the staff member's payslip for the cutoff in which it was approved.
/// </summary>
[ApiController]
[Route("api/reimbursements")]
[Authorize]
public class ReimbursementsController : ControllerBase
{
    private readonly Db _db;
    private readonly AuditService _audit;

    public ReimbursementsController(Db db, AuditService audit)
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
    /// POST /api/reimbursements — file a reimbursement / additional incentive
    /// request (status pending). The amount lands on the payslip once approved.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReimbursementRequest? request)
    {
        if (request is null)
        {
            return StatusCode(422, new { error = "A reimbursement body is required." });
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

        var row = await con.QuerySingleAsync<Reimbursement>(
            """
            insert into reimbursements (user_id, note, amount, approver_id, status)
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

        await _audit.AddAsync(con, tx, uid, "create_reimbursement", "reimbursements", row.Id.ToString(),
            new
            {
                user_id = uid,
                note = request.Note.Trim(),
                amount = request.Amount
            });

        tx.Commit();
        return StatusCode(201, row);
    }
/// <summary>GET /api/reimbursements/mine — the current user's requests, newest first.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var uid = CurrentUserId();
        using var con = _db.Open();

        var rows = await con.QueryAsync<Reimbursement>(
            """
            select * from reimbursements
            where user_id = @Uid::uuid
            order by created_at desc
            """,
            new { Uid = uid });

        return Ok(rows);
    }

    /// <summary>
    /// GET /api/reimbursements/pending — pending reimbursement requests assigned
    /// to this approver, or every pending one when the caller is an HR admin.
    /// </summary>
    [HttpGet("pending")]
    [Authorize(Policy = "ApproverOrAbove")]
    public async Task<IActionResult> Pending()
    {
        var uid = CurrentUserId();
        using var con = _db.Open();

        IEnumerable<Reimbursement> rows;
        if (IsHrAdmin())
        {
            rows = await con.QueryAsync<Reimbursement>(
                """
                select r.*, p.full_name
                from reimbursements r
                join profiles p on p.id = r.user_id
                where r.status = 'pending'
                order by r.created_at asc
                """);
        }
        else
        {
            rows = await con.QueryAsync<Reimbursement>(
                """
                select r.*, p.full_name
                from reimbursements r
                join profiles p on p.id = r.user_id
                where r.status = 'pending' and r.approver_id = @Uid::uuid
                order by r.created_at asc
                """,
                new { Uid = uid });
        }

        return Ok(rows);
    }

    /// <summary>
    /// POST /api/reimbursements/{id}/cancel — HR admin cancels all or part of an
    /// APPROVED reimbursement. The cancelled amount is taken off the payslip
    /// again (the line keeps only its remaining amount). Repeated calls
    /// accumulate; each one is written to audit_log.
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    [Authorize(Policy = "HrAdmin")]
    public async Task<IActionResult> Cancel(long id, [FromBody] CancelReimbursementRequest? request)
    {
        var uid = CurrentUserId();

        if (request?.Amount is null || request.Amount <= 0)
        {
            return StatusCode(422, new { error = "amount must be greater than zero." });
        }
        var amount = Math.Round(request.Amount.Value, 2, MidpointRounding.AwayFromZero);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var rec = await con.QuerySingleOrDefaultAsync<Reimbursement>(
            "select * from reimbursements where id = @Id for update",
            new { Id = id }, tx);
        if (rec is null)
        {
            throw new ApiException(404, "Reimbursement request not found.");
        }
        if (rec.Status != "approved")
        {
            return StatusCode(409, new { error = "Only an approved reimbursement can be cancelled." });
        }

        var alreadyCancelled = rec.CancelledAmount < 0 ? 0m : rec.CancelledAmount;
        var remaining = rec.Amount - alreadyCancelled;
        if (remaining <= 0)
        {
            return StatusCode(409, new { error = "This reimbursement has already been fully cancelled." });
        }
        if (amount > remaining)
        {
            return StatusCode(422, new { error = $"Cannot cancel {amount:0.00}; only {remaining:0.00} remains of this reimbursement." });
        }

        var updated = await con.QuerySingleAsync<Reimbursement>(
            """
            update reimbursements
            set cancelled_amount = coalesce(cancelled_amount, 0) + @Amount,
                cancelled_by = @Uid::uuid,
                cancelled_at = now(),
                cancellation_note = coalesce(@Note, cancellation_note)
            where id = @Id
            returning *
            """,
            new { Amount = amount, Uid = uid, Note = note, Id = id }, tx);

        await _audit.AddAsync(con, tx, uid, "cancel_reimbursement", "reimbursements", updated.Id.ToString(),
            new
            {
                user_id = rec.UserId,
                note = rec.Note,
                approved_amount = rec.Amount,
                cancelled_this_time = amount,
                cancelled_total = updated.CancelledAmount,
                remaining_after = updated.Amount - updated.CancelledAmount,
                reason = note
            });

        tx.Commit();
        return Ok(updated);
    }

    /// <summary>POST /api/reimbursements/{id}/approve — approves the request; the amount is then added to the staff member's payslip.</summary>
    [HttpPost("{id:long}/approve")]
    [Authorize(Policy = "ApproverOrAbove")]
    public async Task<IActionResult> Approve(long id, [FromBody] ResolveRequestRequest? request)
    {
        var uid = CurrentUserId();
        var notes = request?.Notes?.Trim();

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var reimb = await con.QuerySingleOrDefaultAsync<Reimbursement>(
            "select * from reimbursements where id = @Id",
            new { Id = id }, tx);
        if (reimb is null)
        {
            throw new ApiException(404, "Reimbursement request not found.");
        }
        if (reimb.Status != "pending")
        {
            return StatusCode(409, new { error = "This request has already been resolved." });
        }
        if (!IsHrAdmin() && reimb.ApproverId != uid)
        {
            return StatusCode(403, new { error = "You are not the approver assigned to this request." });
        }

        var updated = await con.QuerySingleAsync<Reimbursement>(
            """
            update reimbursements
            set status = 'approved', approver_notes = @Notes, resolved_at = now()
            where id = @Id
            returning *
            """,
            new { Notes = notes, Id = id }, tx);

        await _audit.AddAsync(con, tx, uid, "approve_reimbursement", "reimbursements", updated.Id.ToString(),
            new
            {
                user_id = reimb.UserId,
                note = reimb.Note,
                amount = reimb.Amount,
                notes
            });

        tx.Commit();
        return Ok(updated);
    }

    /// <summary>POST /api/reimbursements/{id}/reject — rejects with a required note.</summary>
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

        var reimb = await con.QuerySingleOrDefaultAsync<Reimbursement>(
            "select * from reimbursements where id = @Id",
            new { Id = id });
        if (reimb is null)
        {
            throw new ApiException(404, "Reimbursement request not found.");
        }
        if (reimb.Status != "pending")
        {
            return StatusCode(409, new { error = "This request has already been resolved." });
        }
        if (!IsHrAdmin() && reimb.ApproverId != uid)
        {
            return StatusCode(403, new { error = "You are not the approver assigned to this request." });
        }

        using var tx = con.BeginTransaction();
        var updated = await con.QuerySingleAsync<Reimbursement>(
            """
            update reimbursements
            set status = 'rejected', approver_notes = @Notes, resolved_at = now()
            where id = @Id
            returning *
            """,
            new { Notes = notes, Id = id }, tx);

        await _audit.AddAsync(con, tx, uid, "reject_reimbursement", "reimbursements", updated.Id.ToString(),
            new { user_id = reimb.UserId, note = reimb.Note, amount = reimb.Amount, notes });

        tx.Commit();
        return Ok(updated);
    }
}