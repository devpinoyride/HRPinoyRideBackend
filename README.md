# Pinoy Ride HR API — ASP.NET Core (.NET 8) Backend

A stateless, CORS-enabled ASP.NET Core 8 Web API for the Pinoy Ride HR Timekeeping
portal. It talks to a Supabase Postgres database with **Dapper/Npgsql**, verifies
credentials against **Supabase Auth**, and issues **its own signed JWTs** (HS256).

> Because a direct Postgres connection (via Npgsql) uses a privileged role,
> **Supabase RLS does not apply** to this API — every endpoint performs its own
> role / ownership checks (policies: `HrAdmin`, `ApproverOrAbove`).

---

## Required environment variables

| Variable | Purpose |
|---|---|
| `DATABASE_URL` | Supabase Postgres connection string. Use the **Session Pooler** host/port `6543` for a serverless-style host like Render (e.g. `postgresql://postgres.<ref>:<password>@aws-0-<region>.pooler.supabase.com:6543/postgres`). |
| `SUPABASE_URL` | Your Supabase project URL, e.g. `https://abcdefg.supabase.co`. |
| `SUPABASE_ANON_KEY` | Supabase **anon/public** key (used for `/auth/v1/token` login). |
| `SUPABASE_SERVICE_ROLE_KEY` | Supabase **service role** key (used for `/auth/v1/admin/users` when HR creates staff). **Never expose this to the frontend.** |
| `JWT_SIGNING_KEY` | A long random string (≥32 chars) used to sign/verify this API's JWTs. |
| `ALLOWED_ORIGIN` | The frontend's origin for CORS. Omit (or set to `*`) for local development. |

Any of these can be supplied as real environment variables or in `appsettings.json`
— they are read through `builder.Configuration`, never hard-coded.

## Run locally

```powershell
dotnet restore
$env:DATABASE_URL = "postgresql://...:5432/postgres"
$env:SUPABASE_URL = "https://<ref>.supabase.co"
$env:SUPABASE_ANON_KEY = "<anon key>"
$env:SUPABASE_SERVICE_ROLE_KEY = "<service role key>"
$env:JWT_SIGNING_KEY = "<a very long random secret>"
$env:ALLOWED_ORIGIN = "http://localhost:5173"
dotnet run
```

The API listens on `PORT` when it is set (e.g. `PORT=5000`), otherwise Kestrel's
default (`http://localhost:5000`). Swagger is available at
`/swagger` in the Development environment only.

## Endpoints

| Method | Path | Access |
|---|---|---|
| POST | `/auth/login` | anonymous |
| POST | `/api/clock/in` | authenticated (self) |
| POST | `/api/clock/out` | authenticated (self) |
| GET | `/api/clock/today` | authenticated (self) |
| POST | `/api/requests` | authenticated (self) |
| GET | `/api/requests/mine` | authenticated (self) |
| GET | `/api/approvals` | `ApproverOrAbove` |
| POST | `/api/approvals/{id}/approve` | `ApproverOrAbove` |
| POST | `/api/approvals/{id}/reject` | `ApproverOrAbove` |
| GET | `/api/staff` | `HrAdmin` |
| POST | `/api/staff` | `HrAdmin` |
| PUT | `/api/staff/{id}` | `HrAdmin` |
| POST | `/api/staff/{id}/deactivate` | `HrAdmin` |
| GET | `/api/reports?from=&to=&staffId=` | `ApproverOrAbove` (approvers: own staff only) |
| GET | `/api/reports/export?from=&to=&staffId=` | `ApproverOrAbove` (CSV download) |

All authenticated calls must send `Authorization: Bearer <token>`.

## Error handling

Errors are returned as ProblemDetails-style JSON — `{ "error": "..." }` — with correct
status codes (401 unauth, 403 forbidden, 404 not found, 409 conflict, 422 validation,
500 unexpected). A global `ExceptionHandlingMiddleware` converts raw Npgsql/DB
exceptions so they never reach the client.

## Audit logging

After every successful insert/update the API writes an `audit_log` row
(`actor_id`, `action`, `table_name`, `record_id`, `details` jsonb) on the same
connection/transaction — server-side only.

## Schema

The Supabase schema (tables `profiles`, `time_entries`, `timekeeping_requests`,
`audit_log` and the six enums) is documented in [`schema.sql`](schema.sql). It is
published there and should already exist in your project. Column mappings used by
this API match it exactly.

For a quick local demo, run [`seed.sql`](seed.sql) after the schema — it creates
5 Auth users (demo password `PinoyRide123!`), matching profiles, time entries for
the current week, and a few pending/approved/rejected requests so the approver
and HR admin views have data to show.

## Deploying to Render

1. Push this folder to a git repo and create a **new Blueprint** from `render.yaml`
   (or a new **Docker web service** pointing at this repo's `Dockerfile`).
2. All secrets are marked `sync: false`, so Render prompts you to paste them in the
   dashboard rather than committing them.
3. Set `ALLOWED_ORIGIN` to your frontend's deployed URL (comma-separated list supported).
4. Suggested env values:
   - `DATABASE_URL`: Supabase **Session Pooler** connection string (port `6543`).