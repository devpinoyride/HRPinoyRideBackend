using Dapper;
using Npgsql;
using PinoyRideHrApi.Infrastructure;

namespace PinoyRideHrApi.Data;

public class Db
{
    private readonly string? _connectionString;

    public Db(IConfiguration config)
    {
        _connectionString = config["DATABASE_URL"];
    }

    public NpgsqlConnection Open()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new ApiException(500, "DATABASE_URL is not configured.");
        }

        var con = new NpgsqlConnection(_connectionString);
        con.Open();
        return con;
    }

    /// <summary>
    /// Idempotent schema additions for databases created before a feature
    /// shipped. schema.sql remains the full reference; this keeps existing
    /// databases current without a manual SQL-editor step. Covers the payroll
    /// salary columns, the per-staff incentive columns, and time_entries.work_setup.
    /// </summary>
    public async Task EnsureAdditionsAsync()
    {
        using var con = Open();
        await con.ExecuteAsync(
            """
            alter table public.profiles
                add column if not exists basic_salary numeric(12, 2);

            alter table public.profiles
                add column if not exists salary_mode text not null default 'basic'
                check (salary_mode in ('basic', 'daily'));

            alter table public.profiles
                add column if not exists daily_rate numeric(12, 2);

            alter table public.profiles
                add column if not exists office_incentive_enabled boolean not null default true;
            alter table public.profiles
                add column if not exists office_incentive_amount numeric(12, 2) not null default 100;
            alter table public.profiles
                add column if not exists mobile_incentive_enabled boolean not null default true;
            alter table public.profiles
                add column if not exists mobile_incentive_amount numeric(12, 2) not null default 100;
            alter table public.profiles
                add column if not exists work_days text not null default 'mon_fri'
                    check (work_days in ('mon_fri', 'mon_sat'));
            alter table public.profiles
                add column if not exists fixed_salary boolean not null default false;

            do $$
            begin
              if not exists (select 1 from pg_type where typname = 'work_setup') then
                create type public.work_setup as enum ('office', 'wfh');
              end if;
            end
            $$;

            alter table public.time_entries
                add column if not exists work_setup public.work_setup not null default 'office';
            """);
    }
}