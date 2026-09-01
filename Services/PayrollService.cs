using Dapper;
using PinoyRideHrApi.Data;
using PinoyRideHrApi.Infrastructure;
using PinoyRideHrApi.Models;

namespace PinoyRideHrApi.Services;

/// <summary>
/// Semi-monthly payroll for Philippines-style cutoffs:
///   cutoff 1 → 1st–15th of the month
///   cutoff 2 → 16th–end of the month
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
///   sunday pay         = daily rate × approved Sundays worked (by request)
///   net pay            = semi-monthly basic − absence deduction + overtime pay
///                         + office incentive + mobile incentive + sunday pay
///
/// DAILY mode (salary_mode = 'daily', for staff paid per day worked, e.g. ₱850/day):
///   daily rate         = daily_rate field directly
///   semi-monthly basic = daily rate × worked days (paid only for days worked)
///   absence deduction  = none (daily-paid staff are not deducted for absences)
///   overtime pay       = OT hours × (daily rate ÷ 8) × 1.25
///   office incentive   = per-staff rate × present office workdays (₱0 if disabled)
///   mobile incentive   = per-staff rate × weeks the staff actually worked
///                        (present or paid leave; ₱0 if disabled)
///   sunday pay         = daily rate × approved Sundays worked (by request)
///   net pay            = semi-monthly basic + overtime pay
///                         + office incentive + mobile incentive + sunday pay
///
/// Office and mobile incentives are configured per staff on the Staff page
/// (toggle + editable peso amount); disabled incentives contribute ₱0 but are
/// still shown on the payslip for transparency.
///
/// Workdays follow each staff's work-week pattern (Mon–Fri or Mon–Sat).
/// A workday is absent when the staff has neither
/// a time_entries row nor an approved leave request covering it (approving a
/// leave writes an 'adjusted' entry, so approved leave is never deducted).
/// Overtime hours are counted only on workdays with an APPROVED overtime
/// request, for the hours worked beyond the standard 8-hour day. Days in the
/// future are shown as upcoming and never counted as absences.
/// </summary>
public class PayrollService
{
    /// <summary>Divisor for the daily rate, Mon–Fri schedule (payroll days per month).</summary>
    public const int PayrollDaysPerMonth = 22;

    /// <summary>Divisor for the daily rate, Mon–Sat schedule (payroll days per month).</summary>
    public const int PayrollDaysPerMonthMonSat = 26;

    /// <summary>Standard paid hours in a workday (PH 8-hour day).</summary>
    public const decimal StandardDailyHours = 8m;

    /// <summary>Overtime premium on ordinary workdays (PH Labor Code: +25%).</summary>
    public const decimal OvertimeMultiplier = 1.25m;

    /// <summary>Grace period (minutes) applied to late arrival only.</summary>
    public const int LateGraceMinutes = 15;

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
            throw new ApiException(422, "cutoff must be 1 (1–15) or 2 (16–end of month).");
        }

        if (cutoff == 1)
        {
            // Cutoff 1: 1st–15th of the month.
            return new PayrollPeriod
            {
                Year = year,
                Month = month,
                Cutoff = 1,
                Start = new DateOnly(year, month, 1),
                End = new DateOnly(year, month, 15)
            };
        }

        // Cutoff 2: 16th–end of the month (handles 28/29/30/31-day months).
        var lastDay = DateTime.DaysInMonth(year, month);
        return new PayrollPeriod
        {
            Year = year,
            Month = month,
            Cutoff = 2,
            Start = new DateOnly(year, month, 16),
            End = new DateOnly(year, month, lastDay)
        };
    }

    /// <summary>Which cutoff is currently open (16th and later → cutoff 2).</summary>
    public static int DefaultCutoff(DateOnly today) => today.Day >= 16 ? 2 : 1;

    /// <summary>
    /// Workday dates between the two dates, inclusive, according to the work-week
    /// pattern: "mon_sat" counts Monday–Saturday, anything else Monday–Friday.
    /// </summary>
    public static List<DateOnly> Workdays(DateOnly start, DateOnly end, string workDays = "mon_fri")
    {
        var includeSaturday = string.Equals(workDays, "mon_sat", StringComparison.OrdinalIgnoreCase);
        var days = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Sunday) continue;
            if (d.DayOfWeek == DayOfWeek.Saturday && !includeSaturday) continue;
            days.Add(d);
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
                   mobile_incentive_enabled, mobile_incentive_amount, work_days, fixed_salary,
                   sched_time_in, sched_time_out
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
                   mobile_incentive_enabled, mobile_incentive_amount, work_days, fixed_salary,
                   sched_time_in, sched_time_out
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
        var totalLateMinutes = 0;     // minutes late beyond grace, summed over present days
        var totalEarlyOutMinutes = 0; // minutes left early, summed over present days

        var schedIn = staff.SchedTimeIn;
        var schedOut = staff.SchedTimeOut;

        var workDayPattern = staff.WorkDays ?? "mon_fri";

        // Mondays of the weeks in which the staff actually had a workday
        // (present or on paid leave). Drives the mobile incentive so a week
        // with no attendance never pays.
        var activeWeekMondays = new HashSet<DateOnly>();

        foreach (var day in Workdays(period.Start, period.End, workDayPattern))
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

            // Punctuality (present days only): compare actual vs scheduled.
            //   Late     = time_in later than (schedule in + 15-min grace)
            //   Early-out = time_out earlier than schedule out (no grace)
            // Only counted when the day has a VALID pair of timestamps
            // (clock-out after clock-in). A malformed/negative entry — e.g. an
            // AM/PM mistake making time_out earlier than time_in — is skipped so
            // it never produces a runaway undertime deduction.
            var lateMin = 0;
            var earlyMin = 0;
            var validPair = timeIn.HasValue && timeOut.HasValue && timeOut.Value > timeIn.Value;
            if (status == "present" && validPair)
            {
                var schedInAt = day.ToDateTime(schedIn);
                var schedOutAt = day.ToDateTime(schedOut);

                var lateBy = (timeIn!.Value - schedInAt).TotalMinutes - LateGraceMinutes;
                if (lateBy > 0) lateMin = (int)Math.Round(lateBy, MidpointRounding.AwayFromZero);

                // Undertime is capped at the scheduled workday span so a bad
                // entry can't exceed one day's worth of minutes.
                var scheduledSpanMin = Math.Max(0, (schedOutAt - schedInAt).TotalMinutes);
                var earlyBy = (schedOutAt - timeOut!.Value).TotalMinutes;
                if (earlyBy > 0) earlyMin = (int)Math.Min(scheduledSpanMin, Math.Round(earlyBy, MidpointRounding.AwayFromZero));

                totalLateMinutes += lateMin;
                totalEarlyOutMinutes += earlyMin;
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
                OvertimeHours = overtimeHours,
                LateMinutes = lateMin,
                EarlyOutMinutes = earlyMin
            });
        }

        // ---- Sunday work (by request) --------------------------------------
        // A Sunday is paid only when the staff both has a time entry AND an
        // approved request covering that Sunday. Each qualifying Sunday pays a
        // flat +1 daily rate (added on top of the base pay), regardless of
        // salary mode. Sundays are never part of the Mon–Fri/Mon–Sat base.
        var sundayDays = 0;
        for (var d = period.Start; d <= period.End; d = d.AddDays(1))
        {
            if (d.DayOfWeek != DayOfWeek.Sunday) continue;
            if (d > today) continue;
            var worksSunday = entryDates.Contains(d) && overtimeSet.Contains(d);
            if (!worksSunday) continue;

            sundayDays++;
            activeWeekMondays.Add(WeekMonday(d));
            var sEntry = entries.FirstOrDefault(e => e.WorkDate == d);
            if (sEntry?.WorkSetup == "office") officeAllowanceDays++;

            days.Add(new PayrollDayDetail
            {
                Date = d,
                Weekday = d.ToString("dddd"),
                Status = "sunday",
                TimeIn = PhClock.ToLocal(sEntry?.TimeIn),
                TimeOut = PhClock.ToLocal(sEntry?.TimeOut),
                Source = sEntry?.Source,
                WorkSetup = sEntry?.WorkSetup,
                Hours = (sEntry?.TimeIn is not null && sEntry?.TimeOut is not null)
                    ? Math.Round((PhClock.ToLocal(sEntry.TimeOut)!.Value - PhClock.ToLocal(sEntry.TimeIn)!.Value).TotalHours, 2)
                    : null,
                OvertimeHours = null
            });
        }
        // Keep the day list in date order (Sundays were appended at the end).
        days = days.OrderBy(x => x.Date).ToList();

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
                // as basic ÷ (payroll days per month); absence deduction applies per
                // absent workday. Mon–Sat schedules have more payroll days, so the
                // divisor grows accordingly (keeps the per-day deduction fair).
                var basic = staff.BasicSalary!.Value;
                var daysPerMonth = string.Equals(workDayPattern, "mon_sat", StringComparison.OrdinalIgnoreCase)
                    ? PayrollDaysPerMonthMonSat
                    : PayrollDaysPerMonth;
                dailyRate = Round(basic / daysPerMonth);

                if (staff.FixedSalary)
                {
                    // Fixed-salary staff always receive their full semi-monthly basic,
                    // regardless of attendance — no absence deduction, ever.
                    semiMonthly = Round(basic / 2m);
                    deduction = 0;
                }
                else if (countedWorkdays == 0)
                {
                    // The cutoff has not started yet (all workdays are in the future).
                    // Nothing has been earned, so pay nothing rather than full basic.
                    semiMonthly = 0;
                    deduction = 0;
                }
                else
                {
                    semiMonthly = Round(basic / 2m);

                    // Absence deduction per absent workday. (Staff with zero clock-ins
                    // are still deducted; use the Fixed salary flag to exempt them.)
                    deduction = Round(dailyRate * absent);
                }
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

            // Sunday work (by request): flat +1 daily rate per approved Sunday worked,
            // added on top of the base pay in every salary mode.
            var sundayPay = Round(dailyRate * sundayDays);

            // Tardiness / undertime: pro-rata by the minute (daily rate ÷ 8h ÷ 60).
            // Fixed-salary staff are exempt (consistent with "always full pay").
            var minuteRate = Round(dailyRate / StandardDailyHours / 60m);
            var tardyMinutes = totalLateMinutes + totalEarlyOutMinutes;
            var tardinessDeduction = (staff.FixedSalary && salaryMode == "basic")
                ? 0m
                : Round(minuteRate * tardyMinutes);

            // Pay before the tardiness deduction. Cap the deduction so net pay
            // never goes negative from tardiness/undertime.
            var payBeforeTardiness = semiMonthly - deduction + overtimePay + officeAllowance + mobileAllowance + sundayPay;
            if (tardinessDeduction > payBeforeTardiness && payBeforeTardiness > 0)
            {
                tardinessDeduction = payBeforeTardiness;
            }

            var netPay = payBeforeTardiness - tardinessDeduction;
            if (netPay < 0) netPay = 0;

            computation = new PayrollComputation
            {
                SalaryMode = salaryMode,
                WorkDayPattern = workDayPattern,
                FixedSalary = staff.FixedSalary && salaryMode == "basic",
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
                SundayDays = sundayDays,
                SundayPay = sundayPay,
                LateMinutes = totalLateMinutes,
                EarlyOutMinutes = totalEarlyOutMinutes,
                TardinessDeduction = tardinessDeduction,
                MinuteRate = minuteRate,
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