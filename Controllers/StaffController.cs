using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;
using PinoyRideHrApi.Services;

namespace PinoyRideHrApi.Controllers;

[ApiController]
[Route("api/staff")]
[Authorize(Policy = "HrAdmin")]
public class StaffController : ControllerBase
{
    private static readonly HashSet<string> AllowedRoles =
        new(StringComparer.OrdinalIgnoreCase) { "employee", "approver", "hr_admin" };

    private readonly Db _db;
    private readonly AuditService _audit;
    private readonly SupabaseAdminClient _supabaseAdmin;

    public StaffController(Db db, AuditService audit, SupabaseAdminClient supabaseAdmin)
    {
        _db = db;
        _audit = audit;
        _supabaseAdmin = supabaseAdmin;
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

    /// <summary>GET /api/staff — list / search profiles by name, email or department.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? role, [FromQuery] string? status)
    {
        var sql = """
            select p.id, p.email, p.full_name, p.department, p.position, p.role, p.status,
                   p.approver_id, p.basic_salary, p.salary_mode, p.daily_rate,
                   p.office_incentive_enabled, p.office_incentive_amount,
                   p.mobile_incentive_enabled, p.mobile_incentive_amount,
                   a.full_name as approver_name, p.created_at
            from profiles p
            left join profiles a on a.id = p.approver_id
            where 1 = 1
            """;
        var parameters = new DynamicParameters();
        parameters.Add("q", "%" + (q ?? "") + "%");

        if (!string.IsNullOrWhiteSpace(role))
        {
            sql += " and p.role = @Role::user_role";
            parameters.Add("role", role.Trim().ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            sql += " and p.status = @Status::user_status";
            parameters.Add("status", status.Trim().ToLowerInvariant());
        }

        sql += """
                 and (p.full_name ilike @q or p.email ilike @q or p.department ilike @q or @q = '%%')
               order by p.full_name asc
            """;

        using var con = _db.Open();
        var rows = await con.QueryAsync<Profile>(sql, parameters);
        return Ok(rows);
    }

    /// <summary>
    /// POST /api/staff — invites the user through Supabase Auth's admin API,
    /// then inserts the profiles row keyed on the returned auth user id.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateStaffRequest? request)
    {
        var uid = CurrentUserId();

        if (request is null)
        {
            return StatusCode(422, new { error = "A staff body is required." });
        }
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return StatusCode(422, new { error = "A valid email is required." });
        }
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return StatusCode(422, new { error = "password must be at least 8 characters." });
        }
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            return StatusCode(422, new { error = "full_name is required." });
        }

        var role = (request.Role ?? "employee").Trim();
        if (!AllowedRoles.Contains(role))
        {
            return StatusCode(422, new { error = $"role must be one of: {string.Join(", ", AllowedRoles)}." });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var authId = await _supabaseAdmin.CreateUserAsync(email, request.Password);

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        Profile row;
        try
        {
            row = await con.QuerySingleAsync<Profile>(
                """
                insert into profiles (id, email, full_name, department, position, role, status, approver_id, basic_salary, salary_mode, daily_rate,
                                      office_incentive_enabled, office_incentive_amount, mobile_incentive_enabled, mobile_incentive_amount)
                values (@Id::uuid, @Email, @FullName, @Department, @Position, @Role::user_role, 'active', @ApproverId::uuid, @BasicSalary, @SalaryMode, @DailyRate,
                        @OfficeIncentiveEnabled, @OfficeIncentiveAmount, @MobileIncentiveEnabled, @MobileIncentiveAmount)
                returning *
                """,
                new
                {
                    Id = authId,
                    Email = email,
                    FullName = request.FullName.Trim(),
                    Department = request.Department?.Trim(),
                    Position = request.Position?.Trim(),
                    Role = role,
                    ApproverId = request.ApproverId,
                    BasicSalary = request.BasicSalary,
                    SalaryMode = request.SalaryMode ?? "basic",
                    DailyRate = request.DailyRate,
                    OfficeIncentiveEnabled = request.OfficeIncentiveEnabled ?? true,
                    OfficeIncentiveAmount = request.OfficeIncentiveAmount ?? 100m,
                    MobileIncentiveEnabled = request.MobileIncentiveEnabled ?? true,
                    MobileIncentiveAmount = request.MobileIncentiveAmount ?? 100m
                }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new ApiException(409, "An account with that email or id already exists.");
        }

        await _audit.AddAsync(con, tx, uid, "create_staff", "profiles", row.Id.ToString(),
            new
            {
                user_id = row.Id,
                email = row.Email,
                role = row.Role,
                department = row.Department,
                position = row.Position,
                approver_id = row.ApproverId,
                invited = true
            });

        tx.Commit();
        return StatusCode(201, row);
    }

    /// <summary>PUT /api/staff/{id} — edit department / position / role / approver.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateStaffRequest? request)
    {
        var uid = CurrentUserId();

        if (request is null)
        {
            return StatusCode(422, new { error = "A staff body is required." });
        }

        var role = (request.Role ?? "").Trim();
        if (role != "" && !AllowedRoles.Contains(role))
        {
            return StatusCode(422, new { error = $"role must be one of: {string.Join(", ", AllowedRoles)}." });
        }

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var row = await con.QuerySingleOrDefaultAsync<Profile>(
            """
            update profiles
            set department = @Department,
                position = @Position,
                role = coalesce(nullif(@Role, ''), role::text)::user_role,
                approver_id = @ApproverId,
                basic_salary = @BasicSalary,
                salary_mode = @SalaryMode,
                daily_rate = @DailyRate,
                office_incentive_enabled = coalesce(@OfficeIncentiveEnabled, office_incentive_enabled),
                office_incentive_amount = coalesce(@OfficeIncentiveAmount, office_incentive_amount),
                mobile_incentive_enabled = coalesce(@MobileIncentiveEnabled, mobile_incentive_enabled),
                mobile_incentive_amount = coalesce(@MobileIncentiveAmount, mobile_incentive_amount)
            where id = @Id::uuid
            returning *
            """,
            new
            {
                Id = id,
                Department = request.Department?.Trim(),
                Position = request.Position?.Trim(),
                Role = role,
                ApproverId = request.ApproverId,
                BasicSalary = request.BasicSalary,
                SalaryMode = request.SalaryMode ?? "basic",
                DailyRate = request.DailyRate,
                OfficeIncentiveEnabled = request.OfficeIncentiveEnabled,
                OfficeIncentiveAmount = request.OfficeIncentiveAmount,
                MobileIncentiveEnabled = request.MobileIncentiveEnabled,
                MobileIncentiveAmount = request.MobileIncentiveAmount
            }, tx);

        if (row is null)
        {
            throw new ApiException(404, "Staff member not found.");
        }

        await _audit.AddAsync(con, tx, uid, "update_staff", "profiles", row.Id.ToString(),
            new
            {
                department = row.Department,
                position = row.Position,
                role = row.Role,
                approver_id = row.ApproverId
            });

        tx.Commit();
        return Ok(row);
    }

    /// <summary>POST /api/staff/{id}/deactivate — set status = 'inactive'.</summary>
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var uid = CurrentUserId();
        if (id == uid)
        {
            throw new ApiException(409, "You cannot deactivate your own account.");
        }

        using var con = _db.Open();
        using var tx = con.BeginTransaction();

        var row = await con.QuerySingleOrDefaultAsync<Profile>(
            """
            update profiles
            set status = 'inactive'
            where id = @Id::uuid
            returning *
            """,
            new { Id = id }, tx);

        if (row is null)
        {
            throw new ApiException(404, "Staff member not found.");
        }

        await _audit.AddAsync(con, tx, uid, "deactivate_staff", "profiles", row.Id.ToString(),
            new { user_id = row.Id });

        tx.Commit();
        return Ok(row);
    }
}