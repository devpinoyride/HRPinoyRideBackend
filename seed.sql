-- =============================================================================
-- Pinoy Ride HR Timekeeping — Supabase SEED DATA
-- =============================================================================
-- Run AFTER schema.sql in the Supabase SQL Editor. Script is idempotent
-- (safe to re-run: ON CONFLICT / existence guards).
--
-- Demo accounts — password for ALL is:  PinoyRide123!
--
--   Role      | Email                       | Full name
--   ----------+-----------------------------+------------------
--   hr_admin  | admin@pinoyride.ph          | HR Administrator
--   approver  | maria.santos@pinoyride.ph   | Maria Santos
--   employee  | juan.delacruz@pinoyride.ph  | Juan Dela Cruz
--   employee  | ana.reyes@pinoyride.ph      | Ana Reyes
--   employee  | pedro.lim@pinoyride.ph      | Pedro Lim
--
-- Login goes through Supabase Auth (email + password) exactly as the C# API
-- does (POST {SUPABASE_URL}/auth/v1/token?grant_type=password), so each
-- row must exist in BOTH auth.users and auth.identities.
-- =============================================================================

begin;

-- pgcrypto gives us bcrypt (crypt/gen_salt) to hash passwords the same way
-- Supabase Auth expects (Supabase ships this extension by default).
create extension if not exists pgcrypto with schema extensions;

-- ---------------------------------------------------------------------------
-- 1) Auth users + email identities
-- ---------------------------------------------------------------------------
create or replace function hr_seed_auth_user(p_id uuid, p_email text, p_password text)
returns void
language plpgsql
security definer
set search_path = public, extensions
as $$
begin
  -- Create the auth.users row (skip if a row with this id already exists).
  insert into auth.users (
    instance_id, id, aud, role, email, encrypted_password,
    email_confirmed_at, confirmation_token, recovery_token,
    email_change_token_new, email_change,
    raw_app_meta_data, raw_user_meta_data,
    is_super_admin, is_sso_user, created_at, updated_at
  )
  values (
    '00000000-0000-0000-0000-000000000000',
    p_id, 'authenticated', 'authenticated', p_email,
    crypt(p_password, gen_salt('bf')),
    now(), '', '', '', '',
    '{"provider":"email","providers":["email"]}',
    jsonb_build_object('email', p_email),
    false, false, now(), now()
  )
  on conflict (id) do nothing;

  -- Email identity link: required by GoTrue's password flow. Some Supabase
  -- versions auto-create this row via a trigger; guard against duplicates.
  insert into auth.identities (
    id, user_id, provider_id, identity_data, provider,
    last_sign_in_at, created_at, updated_at, email
  )
  select
    p_id::text, p_id, p_id::text,
    jsonb_build_object('sub', p_id::text, 'email', p_email),
    'email', now(), now(), now(), p_email
  where not exists (
    select 1 from auth.identities i
    where i.user_id = p_id and i.provider = 'email'
  );
end;
$$;

select hr_seed_auth_user('11111111-1111-4111-8111-111111111111', 'admin@pinoyride.ph',          'PinoyRide123!');
select hr_seed_auth_user('22222222-2222-4222-8222-222222222222', 'maria.santos@pinoyride.ph',   'PinoyRide123!');
select hr_seed_auth_user('33333333-3333-4333-8333-333333333333', 'juan.delacruz@pinoyride.ph',  'PinoyRide123!');
select hr_seed_auth_user('44444444-4444-4444-8444-444444444444', 'ana.reyes@pinoyride.ph',      'PinoyRide123!');
select hr_seed_auth_user('55555555-5555-4555-8555-555555555555', 'pedro.lim@pinoyride.ph',      'PinoyRide123!');

-- ---------------------------------------------------------------------------
-- 2) profiles (profiles.id = auth.users.id)
-- ---------------------------------------------------------------------------
insert into public.profiles (id, email, full_name, department, position, role, status, approver_id)
values
  ('11111111-1111-4111-8111-111111111111', 'admin@pinoyride.ph',          'HR Administrator', 'Human Resources', 'HR Manager',   'hr_admin', 'active', null),
  ('22222222-2222-4222-8222-222222222222', 'maria.santos@pinoyride.ph',   'Maria Santos',     'Operations',       'Team Lead',    'approver', 'active', '11111111-1111-4111-8111-111111111111'),
  ('33333333-3333-4333-8333-333333333333', 'juan.delacruz@pinoyride.ph',  'Juan Dela Cruz',   'Operations',       'Rider Support', 'employee', 'active', '22222222-2222-4222-8222-222222222222'),
  ('44444444-4444-4444-8444-444444444444', 'ana.reyes@pinoyride.ph',      'Ana Reyes',        'Operations',       'Rider Support', 'employee', 'active', '22222222-2222-4222-8222-222222222222'),
  ('55555555-5555-4555-8555-555555555555', 'pedro.lim@pinoyride.ph',      'Pedro Lim',        'Finance',          'Accounting Staff', 'employee', 'active', '22222222-2222-4222-8222-222222222222')
on conflict (id) do nothing;
-- ---------------------------------------------------------------------------
-- 3) time_entries
--    work dates are relative to TODAY in Asia/Manila (offs below are days back).
--    time_in/time_out are UTC instants whose wall-clock time in Manila equals
--    the start/end times below (mirrors the C# PhClock.Combine logic).
-- ---------------------------------------------------------------------------
with md as (select (now() at time zone 'Asia/Manila')::date as d)
insert into public.time_entries (user_id, work_date, time_in, time_out, source, status)
select
  v.u::uuid,
  md.d - v.offs,
  ((md.d - v.offs) + v.start_t) at time zone 'Asia/Manila',
  ((md.d - v.offs) + v.end_t) at time zone 'Asia/Manila',
  v.src::public.entry_source,
  v.st::public.entry_status
from md
cross join (values
  -- Juan Dela Cruz: full days + an adjusted overtime day + today (clocked in, open)
  ('33333333-3333-4333-8333-333333333333', 3, time '09:00', time '18:00', 'self_logged', 'confirmed'),
  ('33333333-3333-4333-8333-333333333333', 2, time '09:00', time '18:00', 'self_logged', 'confirmed'),
  ('33333333-3333-4333-8333-333333333333', 1, time '09:00', time '20:00', 'adjusted',    'confirmed'),
  ('33333333-3333-4333-8333-333333333333', 0, time '09:00', null,         'self_logged', 'confirmed'),
  -- Ana Reyes: full days + today open; her d-2 entry is the one she asks to adjust
  ('44444444-4444-4444-8444-444444444444', 2, time '09:00', time '18:00', 'self_logged', 'confirmed'),
  ('44444444-4444-4444-8444-444444444444', 1, time '09:00', time '18:00', 'self_logged', 'confirmed'),
  ('44444444-4444-4444-8444-444444444444', 0, time '09:00', null,         'self_logged', 'confirmed'),
  -- Pedro Lim: one full day
  ('55555555-5555-4555-8555-555555555555', 1, time '09:00', time '18:00', 'self_logged', 'confirmed'),
  -- Maria Santos (approver) and the HR admin worked yesterday
  ('22222222-2222-4222-8222-222222222222', 1, time '09:00', time '18:00', 'self_logged', 'confirmed'),
  ('11111111-1111-4111-8111-111111111111', 1, time '09:00', time '18:00', 'self_logged', 'confirmed')
) as v(u, offs, start_t, end_t, src, st);

-- ---------------------------------------------------------------------------
-- 4) timekeeping_requests
-- ---------------------------------------------------------------------------
-- Pending #1: Ana Reyes asks to correct d-2 (arrived early, left early).
with md as (select (now() at time zone 'Asia/Manila')::date as d)
insert into public.timekeeping_requests
  (user_id, work_date, requested_time_in, requested_time_out, request_type, reason, approver_id, status)
select
  '44444444-4444-4444-8444-444444444444'::uuid,
  md.d - 2,
  time '08:00', time '17:00',
  'adjustment',
  'Left early for a medical appointment; request to correct the recorded shift.',
  '22222222-2222-4222-8222-222222222222'::uuid,
  'pending'
from md;

-- Pending #2: Pedro Lim asks for leave today.
with md as (select (now() at time zone 'Asia/Manila')::date as d)
insert into public.timekeeping_requests
  (user_id, work_date, requested_time_in, requested_time_out, request_type, reason, approver_id, status)
select
  '55555555-5555-4555-8555-555555555555'::uuid,
  md.d,
  time '08:30', time '17:30',
  'leave',
  'Family errand - request approval for a half-day leave.',
  '22222222-2222-4222-8222-222222222222'::uuid,
  'pending'
from md;

-- Approved #3: Juan Dela Cruz overtime for d-1 (matches the adjusted entry above).
with md as (select (now() at time zone 'Asia/Manila')::date as d)
insert into public.timekeeping_requests
  (user_id, work_date, requested_time_in, requested_time_out, request_type, reason, approver_id, status, approver_notes, resolved_at)
select
  '33333333-3333-4333-8333-333333333333'::uuid,
  md.d - 1,
  time '09:00', time '20:00',
  'overtime',
  'Extended shift to cover the night dispatch queue.',
  '22222222-2222-4222-8222-222222222222'::uuid,
  'approved',
  'Approved - thank you for covering dispatch.',
  ((md.d - 1) + time '20:30') at time zone 'Asia/Manila'
from md;

-- Rejected #4: Pedro Lim leave request for d-1.
with md as (select (now() at time zone 'Asia/Manila')::date as d)
insert into public.timekeeping_requests
  (user_id, work_date, requested_time_in, requested_time_out, request_type, reason, approver_id, status, approver_notes, resolved_at)
select
  '55555555-5555-4555-8555-555555555555'::uuid,
  md.d - 1,
  time '09:00', time '17:30',
  'leave',
  'Requested time off for a personal appointment.',
  '22222222-2222-4222-8222-222222222222'::uuid,
  'rejected',
  'Insufficient staff coverage that day - please resubmit for another date.',
  ((md.d - 1) + time '22:00') at time zone 'Asia/Manila'
from md;

-- ---------------------------------------------------------------------------
-- 5) audit_log (server-side history — the API writes these at runtime too)
-- ---------------------------------------------------------------------------
insert into public.audit_log (actor_id, action, table_name, record_id, details)
values
  ('11111111-1111-4111-8111-111111111111', 'create_staff',  'profiles',             '33333333-3333-4333-8333-333333333333', '{"email":"juan.delacruz@pinoyride.ph","role":"employee"}'::jsonb),
  ('11111111-1111-4111-8111-111111111111', 'create_staff',  'profiles',             '44444444-4444-4444-8444-444444444444', '{"email":"ana.reyes@pinoyride.ph","role":"employee"}'::jsonb),
  ('11111111-1111-4111-8111-111111111111', 'create_staff',  'profiles',             '55555555-5555-4555-8555-555555555555', '{"email":"pedro.lim@pinoyride.ph","role":"employee"}'::jsonb),
  ('22222222-2222-4222-8222-222222222222', 'approve_request', 'timekeeping_requests', '3', '{"user_id":"33333333-3333-4333-8333-333333333333","status":"approved"}'::jsonb),
  ('22222222-2222-4222-8222-222222222222', 'reject_request', 'timekeeping_requests',  '4', '{"user_id":"55555555-5555-4555-8555-555555555555","status":"rejected"}'::jsonb);

-- ---------------------------------------------------------------------------
-- 6) Quick verification
-- ---------------------------------------------------------------------------
select p.role, p.email, p.full_name, p.status, count(te.id) as entries
from public.profiles p
left join public.time_entries te on te.user_id = p.id
group by p.id, p.role, p.email, p.full_name, p.status
order by p.role, p.email;

select r.status, count(*) as requests
from public.timekeeping_requests r
group by r.status
order by r.status;

commit;