using Dapper;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Services;

/// <summary>
/// Semi-monthly payroll for Philippines-style cutoffs:
///   cutoff 1 → 11th–25th of the month
///   cutoff 2 → 26th of the month – 10th of the next month
///
/// Computation rules (shown on the payslip so they stay transparent):
///
/// BASIC mode (salary_mode = 'basic', the default):
///   daily rate         = basic salary ÷ 22
///   semi-monthly basic = basic salary ÷ 2
///   absence deduction  = daily rate × absent workdays (fixed-salary staff who
///                        clock in for zero days are not deducted)
///   overtime pay       = OT hours × (daily rate ÷ 8) × 1.25
///   office incentive   = per-staff rate × present office workdays (₱0 if disabled)
///   mobile incentive   = per-staff rate × weeks the staff actually worked
///                        (present or paid leave; ₱0 if disabled)
///   net pay            = semi-monthly basic − absence deduction + overtime pay
///                         + office incentive + mobile incentive
///
/// DAILY mode (salary_mode = 'daily', for staff paid per day worked, e.g. ₱850/day):
///   daily rate         = daily_rate field directly
///   semi-monthly basic = daily rate × worked days (paid only for days worked)
///   absence deduction  = none (daily-paid staff are not deducted for absences)
///   overtime pay       = OT hours × (daily rate ÷ 8) × 1.25
///   office incentive   = per-staff rate × present office workdays (₱0 if disabled)
///   mobile incentive   = per-staff rate × weeks the staff actually worked
///                        (present or paid leave; ₱0 if disabled)
///   net pay            = semi-monthly basic + overtime pay
///                         + office incentive + mobile incentive
///
/// Office and mobile incentives are configured per staff on the Staff page
/// (toggle + editable peso amount); disabled incentives contribute ₱0 but are
/// still shown on the payslip for transparency.
///
/// Workdays are Monday–Friday. A workday is absent when the staff has neither
/// a time_entries row nor an approved leave request covering it (approving a
/// leave writes an 'adjusted' entry, so approved leave is never deducted).
/// Overtime hours are counted only on workdays with an APPROVED overtime
/// request, for the hours worked beyond the standard 8-hour day. Days in the
/// future are shown as upcoming and never counted as absences.
/// </summary>
public class PayrollService
{
    /// <summary>Divisor for the daily rate (payroll days per month).</summary>
    public const int PayrollDaysPerMonth = 22;

    /// <summary>Standard paid hours in a workday (PH 8-hour day).</summary>
    public const decimal StandardDailyHours = 8m;

    /// <summary>Overtime premium on ordinary workdays (PH Labor Code: +25%).</summary>
    public const decimal OvertimeMultiplier = 1.25m;

    private readonly Db _db;

    public PayrollService(Db db) => _db = db;

    /// <summary>Resolves the cutoff dates, validating the request parameters.</summary>
    public static PayrollPeriod ResolvePeriod(int year, int month, int cutoff)
    {
        if (year is < 2000 or > 2100)
        {
            throw new ApiException(422, "year must be between 2000 and 2100.");
        }
        if (month is < 1 or > 12)
        {
            throw new ApiException(422, "month must be between 1 and 12.");
        }
        if (cutoff is not (1 or 2))
        {
            throw new ApiException(422, "cutoff must be 1 (11–25) or 2 (26–10).");
        }

        if (cutoff == 1)
        {
            return new PayrollPeriod
            {
                Year = year,
                Month = month,
                Cutoff = 1,
                Start = new DateOnly(year, month, 11),
                End = new DateOnly(year, month, 25)
            };
        }

        var next = month == 12 ? new DateOnly(year + 1, 1, 10) : new DateOnly(year, month + 1, 10);
        return new PayrollPeriod
        {
            Year = year,
            Month = month,
            Cutoff = 2,
            Start = new DateOnly(year, month, 26),
            End = next
        };
    }

    /// <summary>Which cutoff is currently open (26th and later → cutoff 2).</summary>
    public static int DefaultCutoff(DateOnly today) => today.Day >= 26 ? 2 : 1;

    /// <summary>Monday–Friday dates between the two dates, inclusive.</summary>
    public static List<DateOnly> Workdays(DateOnly start, DateOnly end)
    {
        var days = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                days.Add(d);
            }
        }
        return days;
    }

    /// <summary>All staff members (payslips can be produced for anyone on file).</summary>
    public async Task<List<Profile>> GetStaffAsync()
    {
        using var con = _db.Open();
        var rows = await con.QueryAsync<Profile>(
            """
            select id, email, full_name, department, position, role, status, approver_id, basic_salary,
                   salary_mode, daily_rate,
                   office_incentive_enabled, office_incentive_amount,
                   mobile_incentive_enabled, mobile_incentive_amount
            from profiles
            order by full_name asc
            """);
        return rows.AsList();
    }

    /// <summary>Loads one staff member, or null when the id is unknown.</summary>
    public async Task<Profile?> GetStaffAsync(Guid id)
    {
        using var con = _db.Open();
        return await con.QuerySingleOrDefaultAsync<Profile>(
            """
            select id, email, full_name, department, position, role, status, approver_id, basic_salary,
                   salary_mode, daily_rate,
                   office_incentive_enabled, office_incentive_amount,
                   mobile_incentive_enabled, mobile_incentive_amount
            from profiles
            where id = @Id::uuid
            """,
            new { Id = id });
    }

    /// <summary>
    /// Computes one staff member's attendance breakdown and pay for the period.
    /// When no basic salary is set, Computation stays null so the payslip can
    /// still show the attendance breakdown.
    /// </summary>
    public async Task<PayrollPayslip> ComputeAsync(Profile staff, PayrollPeriod period)
    {
        var today = PhClock.Today;

        using var con = _db.Open();
        var entries = (await con.QueryAsync<TimeEntry>(
            """
            select id, user_id, work_date, time_in, time_out, source, status, work_setup::text as work_setup
            from time_entries
            where user_id = @Uid::uuid and work_date between @From and @To
            order by work_date asc
            """,
            new { Uid = staff.Id, From = period.Start, To = period.End })).AsList();

        var approvedLeave = (await con.QueryAsync<DateOnly>(
            """
            select work_date
            from timekeeping_requests
            where user_id = @Uid::uuid and request_type = 'leave' and status = 'approved'
              and work_date between @From and @To
            """,
            new { Uid = staff.Id, From = period.Start, To = period.End })).AsList();
        var leaveSet = approvedLeave.ToHashSet();

        var approvedOvertime = (await con.QueryAsync<DateOnly>(
            """
            select work_date
            from timekeeping_requests
            where user_id = @Uid::uuid and request_type = 'overtime' and status = 'approved'
              and work_date between @From and @To
            """,
            new { Uid = staff.Id, From = period.Start, To = period.End })).AsList();
        var overtimeSet = approvedOvertime.ToHashSet();

        var entryDates = entries.Select(e => e.WorkDate).ToHashSet();

        var days = new List<PayrollDayDetail>();
        var worked = 0;
        var paidLeave = 0;
        var absent = 0;
        var countedWorkdays = 0;
        var totalOvertimeHours = 0.0;

        var officeAllowanceDays = 0;  // office workdays the staff was present

        // Mondays of the weeks in which the staff actually had a workday
        // (present or on paid leave). Drives the mobile incentive so a week
        // with no attendance never pays.
        var activeWeekMondays = new HashSet<DateOnly>();

        foreach (var day in Workdays(period.Start, period.End))
        {
            string status;
            if (day > today)
            {
                status = "upcoming";
            }
            else
            {
                countedWorkdays++;
                if (entryDates.Contains(day))
                {
                    status = "present";
                    worked++;
                    activeWeekMondays.Add(WeekMonday(day));
                    var dayEntry = entries.FirstOrDefault(e => e.WorkDate == day);
                    if (dayEntry?.WorkSetup == "office")
                    {
                        officeAllowanceDays++;
                    }
                }
                else if (leaveSet.Contains(day))
                {
                    status = "paid_leave";
                    paidLeave++;
                    activeWeekMondays.Add(WeekMonday(day));
                }
                else
                {
                    status = "absent";
                    absent++;
                }
            }

            var entry = entries.FirstOrDefault(e => e.WorkDate == day);
            DateTime? timeIn = null;
            DateTime? timeOut = null;
            double? hours = null;
            double? overtimeHours = null;
            if (entry is not null)
            {
                timeIn = PhClock.ToLocal(entry.TimeIn);
                timeOut = PhClock.ToLocal(entry.TimeOut);
                if (timeIn.HasValue && timeOut.HasValue)
                {
                    hours = Math.Round((timeOut.Value - timeIn.Value).TotalHours, 2);
                }

                // OT is paid only when the day carries an approved overtime
                // request, and only for hours beyond the standard workday.
                if (status == "present" && overtimeSet.Contains(day) && hours.HasValue && hours.Value > (double)StandardDailyHours)
                {
                    overtimeHours = Math.Round(hours.Value - (double)StandardDailyHours, 2);
                    totalOvertimeHours += overtimeHours.Value;
                }
            }

            days.Add(new PayrollDayDetail
            {
                Date = day,
                Weekday = day.ToString("dddd"),
                Status = status,
                TimeIn = timeIn,
                TimeOut = timeOut,
                Source = entry?.Source,
                WorkSetup = entry?.WorkSetup,
                Hours = hours,
                OvertimeHours = overtimeHours
            });
        }

        PayrollComputation? computation = null;

        var salaryMode = staff.SalaryMode ?? "basic";
        var hasBasic = staff.BasicSalary.HasValue;
        var hasDaily = staff.DailyRate.HasValue;

        // Daily mode requires a daily_rate; basic mode requires a basic_salary.
        // (A staff in daily mode may also have basic_salary set — ignored for payroll.)
        if ((salaryMode == "daily" && hasDaily) || (salaryMode == "basic" && hasBasic))
        {
            decimal dailyRate;
            decimal semiMonthly;
            decimal deduction;

            if (salaryMode == "daily")
            {
                // DAILY mode: paid per day worked. Daily rate comes straight from the
                // daily_rate field (e.g. ₱850/day). No absence deduction — they only
                // earn on days they actually work.
                var daily = staff.DailyRate!.Value;
                dailyRate = Round(daily);
                semiMonthly = Round(daily * worked);
                deduction = 0;
            }
            else
            {
                // BASIC mode: monthly salary, paid semi-monthly. Daily rate is derived
                // as basic ÷ 22; absence deduction applies per absent workday.
                var basic = staff.BasicSalary!.Value;
                dailyRate = Round(basic / PayrollDaysPerMonth);
                semiMonthly = Round(basic / 2m);

                // Fixed-salary staff with zero clock-ins get no deduction; staff who
                // clock in but miss days are deducted for those absent days.
                deduction = (worked == 0 && paidLeave == 0)
                    ? 0
                    : Round(dailyRate * absent);
            }

            var hourlyRate = dailyRate / StandardDailyHours;
            var overtimePay = Round((decimal)totalOvertimeHours * hourlyRate * OvertimeMultiplier);

            // Office incentive: per-staff rate × office workdays the staff was present.
            // Disabled → ₱0 (still shown on the payslip for transparency).
            var officeRate = staff.OfficeIncentiveEnabled ? staff.OfficeIncentiveAmount : 0m;
            var officeAllowance = Round(officeRate * officeAllowanceDays);

            // Mobile incentive: per-staff rate × weeks (Mon–Sun) in which the staff
            // actually had a workday (present or paid leave). A week with no
            // attendance pays nothing. Disabled → ₱0 (still shown for transparency).
            var weeksWithWorkdays = activeWeekMondays.Count;
            var mobileRate = staff.MobileIncentiveEnabled ? staff.MobileIncentiveAmount : 0m;
            var mobileAllowance = Round(mobileRate * weeksWithWorkdays);

            var netPay = semiMonthly - deduction + overtimePay + officeAllowance + mobileAllowance;

            computation = new PayrollComputation
            {
                SalaryMode = salaryMode,
                BasicSalary = staff.BasicSalary ?? 0,
                DailyRate = dailyRate,
                SemiMonthlyBasic = semiMonthly,
                Workdays = countedWorkdays,
                WorkedDays = worked,
                PaidLeaveDays = paidLeave,
                AbsentDays = absent,
                AbsenceDeduction = deduction,
                OvertimeHours = totalOvertimeHours,
                OvertimePay = overtimePay,
                OfficeIncentiveEnabled = staff.OfficeIncentiveEnabled,
                OfficeIncentiveRate = officeRate,
                OfficeIncentiveDays = officeAllowanceDays,
                OfficeAllowance = officeAllowance,
                MobileIncentiveEnabled = staff.MobileIncentiveEnabled,
                MobileIncentiveRate = mobileRate,
                MobileIncentiveWeeks = weeksWithWorkdays,
                MobileAllowance = mobileAllowance,
                NetPay = netPay
            };
        }

        return new PayrollPayslip
        {
            Staff = staff,
            Period = period,
            Days = days,
            Computation = computation
        };
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Monday that starts the Monday–Sunday week containing <paramref name="d"/>.</summary>
    private static DateOnly WeekMonday(DateOnly d) =>
        d.AddDays(-(int)d.DayOfWeek + (d.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
}