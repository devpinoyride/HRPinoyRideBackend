-- =============================================================================
-- Pinoy Ride HR Timekeeping — Supabase (Postgres) schema reference
-- =============================================================================
-- The prompt states this schema "already exists" in the Supabase project; this
-- file documents the exact column names / enums the C# API expects so the schema
-- can be re-applied or audited in the Dashboard's SQL editor.
--
-- NOTE: profiles.id references auth.users(id), i.e. the Supabase Auth user id.
-- =============================================================================

begin;

-- ---- Enums -----------------------------------------------------------------
do $$
begin
  if not exists (select 1 from pg_type where typname = 'user_role') then
    create type user_role as enum ('employee', 'approver', 'hr_admin');
  end if;
  if not exists (select 1 from pg_type where typname = 'user_status') then
    create type user_status as enum ('active', 'inactive');
  end if;
  if not exists (select 1 from pg_type where typname = 'entry_source') then
    create type entry_source as enum ('self_logged', 'adjusted');
  end if;
  if not exists (select 1 from pg_type where typname = 'entry_status') then
    create type entry_status as enum ('confirmed', 'pending');
  end if;
  if not exists (select 1 from pg_type where typname = 'work_setup') then
    create type work_setup as enum ('office', 'wfh');
  end if;
  if not exists (select 1 from pg_type where typname = 'request_type') then
    create type request_type as enum ('adjustment', 'leave', 'overtime', 'other');
  end if;
  if not exists (select 1 from pg_type where typname = 'request_status') then
    create type request_status as enum ('pending', 'approved', 'rejected');
  end if;
end $$;

-- ---- profiles --------------------------------------------------------------
create table if not exists public.profiles (
  id           uuid primary key references auth.users (id) on delete cascade,
  email        text,
  full_name    text not null,
  department   text,
  position     text,
  role         public.user_role   not null default 'employee',
  status       public.user_status not null default 'active',
  approver_id  uuid references public.profiles (id),
  basic_salary numeric(12,2),
  created_at   timestamptz not null default now()
);

-- Payroll: monthly basic salary. Idempotent so databases created before this
-- column existed gain it when schema.sql is re-run.
alter table public.profiles add column if not exists basic_salary numeric(12,2);
alter table public.profiles add column if not exists salary_mode text not null default 'basic'
  check (salary_mode in ('basic', 'daily'));
alter table public.profiles add column if not exists daily_rate numeric(12,2);

-- Payroll incentives (per-staff configurable). Replaces the previously hardcoded
-- ₱100 office/mobile allowance constants. Each incentive has an on/off toggle and
-- an editable peso amount. Idempotent so existing databases gain them on re-run.
--   office_incentive → paid per office workday the staff was present
--   mobile_incentive → paid per week (Mon–Sun) with ≥1 workday in the cutoff
alter table public.profiles add column if not exists office_incentive_enabled boolean not null default true;
alter table public.profiles add column if not exists office_incentive_amount  numeric(12,2) not null default 100;
alter table public.profiles add column if not exists mobile_incentive_enabled boolean not null default true;
alter table public.profiles add column if not exists mobile_incentive_amount  numeric(12,2) not null default 100;

create index if not exists profiles_approver_id_idx on public.profiles (approver_id);
create index if not exists profiles_role_idx        on public.profiles (role);

-- ---- time_entries ----------------------------------------------------------
create table if not exists public.time_entries (
  id         bigserial primary key,
  user_id    uuid not null references public.profiles (id) on delete cascade,
  work_date  date not null,
  time_in    timestamptz,
  time_out   timestamptz,
  source     public.entry_source  not null default 'self_logged',
  status     public.entry_status  not null default 'confirmed',
  work_setup public.work_setup    not null default 'office',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint time_entries_user_date_key unique (user_id, work_date)
);

-- Work location for the day (added for the payroll module; idempotent).
alter table public.time_entries add column if not exists work_setup work_setup not null default 'office';

create index if not exists time_entries_user_date_idx   on public.time_entries (user_id, work_date);
create index if not exists time_entries_work_date_idx   on public.time_entries (work_date);

-- ---- timekeeping_requests --------------------------------------------------
create table if not exists public.timekeeping_requests (
  id                 bigserial primary key,
  user_id            uuid not null references public.profiles (id) on delete cascade,
  work_date          date not null,
  requested_time_in  time,
  requested_time_out time,
  request_type       public.request_type not null,
  reason             text not null,
  approver_id        uuid references public.profiles (id),
  status             public.request_status not null default 'pending',
  approver_notes     text,
  resolved_at        timestamptz,
  created_at         timestamptz not null default now()
);

create index if not exists timekeeping_requests_approver_status_idx on public.timekeeping_requests (approver_id, status);
create index if not exists timekeeping_requests_user_idx            on public.timekeeping_requests (user_id);
create index if not exists timekeeping_requests_work_date_idx       on public.timekeeping_requests (work_date);

-- ---- audit_log -------------------------------------------------------------
create table if not exists public.audit_log (
  id         bigserial primary key,
  actor_id   uuid,
  action     text not null,
  table_name text not null,
  record_id  text,
  details    jsonb,
  created_at timestamptz not null default now()
);

create index if not exists audit_log_actor_idx    on public.audit_log (actor_id);
create index if not exists audit_log_created_idx  on public.audit_log (created_at desc);

commit;