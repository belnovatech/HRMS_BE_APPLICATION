# HRMS backend

ASP.NET Core 9 API with PostgreSQL persistence. The API base path is `/api`.

## Run locally

From `backend`:

```powershell
dotnet restore HRMS.Backend.slnx
dotnet user-secrets set "ConnectionStrings:Database" "Host=localhost;Port=5432;Database=hrms_local;Username=postgres;Password=YOUR_LOCAL_PASSWORD" --project src/HRMS.Api
dotnet run --project src/HRMS.Api --launch-profile https
```

Development HTTPS: `https://localhost:7059`; HTTP: `http://localhost:5236`.
Swagger is enabled only in Development. Pending PostgreSQL migrations run at startup.
The frontend reads `REACT_APP_API_URL`, defaulting to `https://localhost:7059/api`.

Historical development accounts are `admin@hr.com`, `manager@belnova.com`,
`arjun@belnova.com`, and `kavya@belnova.com`. Their historical development password is
`password123`. Startup replaces plaintext password values with salted Identity hashes.
Outside Development, known demo accounts are disabled (including OTP recovery) and their passwords are replaced with random unknown passwords.
For a production administrator, set `Bootstrap:Email` and `Bootstrap:Password` before
first startup, then remove the bootstrap password from the deployment environment.
The bootstrap email must not already exist. Passwords require 12–128 characters with
letters and digits.

## Authentication and access

`POST /auth/login` accepts `{ "identifier": "...", "password": "..." }` and returns
`{ "token": "...", "expiresIn": 28800, "user": { ... } }`.
Send `Authorization: Bearer TOKEN` on subsequent requests. These are opaque random
session tokens, not JWTs. Only their SHA-256 digests are persisted. Sessions expire
after eight hours; `/auth/logout` revokes the current session. Password changes revoke
existing sessions. Disabled employee records cannot authenticate.

All controller endpoints require authentication unless explicitly anonymous. HR manages
administrative resources. Employees access their own records. Managers can view team
attendance, leave, support, and documents and decide team leave requests. Team membership
uses the manager's account ID in `ReportsTo`; never a caller-provided employee header.
Payroll and uploaded file contents remain visible only to their owner and HR.

There are three enforced account roles: `hr`, `manager`, `employee`. `/roles` stores
role configuration records; arbitrary custom permission maps are not an authorization
engine. Do not interpret a saved permission map as an enforced policy.

Authentication endpoints have a per-IP request limit. Five failed password attempts
lock an account for 15 minutes, including attempts through different account aliases.
OTP codes expire after ten minutes, lock after five failed checks, and can be used once.
`POST /auth/verify-otp` consumes a code and creates a session. To reset a password,
submit the code directly to `/auth/reset-password`; do not consume it first.

## Email configuration

Configure these via user-secrets or environment variables (`__` replaces `:`):

- `Email:Host`, `Email:Port` (default 587), `Email:From`
- `Email:Username`, `Email:Password` when required

Delivery uses SMTP with TLS. `POST /auth/request-otp` sends a code to the account's
email address. Codes are never returned by the API or printed to logs. Without email
configuration, the endpoint returns 503. SMTP delivery requires a real provider test.

## Workflow contracts

- Employees: `GET/POST /employees`, `GET/PUT/DELETE /employees/{guid}`. DELETE deactivates
  the record and retains history. Account provisioning is a separate HR operation.
- Accounts: `GET/POST /accounts`, `PUT /accounts/{id}/manager`. When provisioning an
  existing employee, provide their `employeeNumber` and matching email. New employee
  accounts create a matching employee profile.
- Team: `GET /team`, restricted to the caller's visible accounts.
- Attendance: `GET /attendance`, `POST /attendance/check-in`, `/check-out`.
  One open session per employee is enforced by a database constraint.
- Corrections: `GET/POST /attendance/corrections` with `date`, `checkIn`, `checkOut`, and
  `reason`. HR decides at `POST /attendance/corrections/{id}/decision`. Approval applies
  the times to attendance; ambiguous multiple sessions are rejected for review.
- Leave: `GET/POST /leave`, `PATCH /leave/{id}/decision`. Dates cannot overlap pending
  or approved requests. Requests count calendar days and must stay within one year.
  Approval requires a configured entitlement and sufficient balance. Decisions cannot
  be repeated. The decision and balance update are transactional.
- Entitlements: `POST /leave/balances` with `employeeId`, `leaveType`, `year`, `total`;
  `GET /leave/balances` returns computed `used` and `available`. HR maintains policies
  and entitlements. Policy JSON does not yet implement accrual or carry-forward rules.
- Payroll: HR sets `PUT /payroll/salary/{employeeId}` with decimal `basic`, `allowances`,
  `deductions`. `POST /payroll/calculate/{employeeId}` or `/calculate-all` accepts
  `{ "month": 9, "year": 2026 }`. Net pay is basic + allowances - deductions, rounded
  to cents. Missing salary or duplicate employee/month/year returns 409. Calculations
  never run through GET. `POST /payroll/process` requires explicit `payslipIds` and
  allows only Calculated -> Processed. It records approval; it does not transfer money.
- Documents: upload to `POST /files` as multipart `file`, then `POST /documents` with
  `employeeId`, `title`, `category`, `fileName`, `fileId`. HR verifies at
  `PATCH /documents/{id}/status`. Historical metadata without file IDs has no stored file.
- Files: `GET/DELETE /files/{id}`. Uploads are limited to 20 MB and allowed document/image
  extensions. Downloads are attachments. Attached documents prevent file deletion.
- Notifications: `GET/POST /notifications`, `PATCH /notifications/{id}/read`, `/read-all`.
  Read receipts belong to the authenticated account; broadcasts are not marked read for
  other recipients.
- Support: `GET/POST /support/tickets`, HR `PATCH /support/tickets/{id}`.
- Organization: CRUD `/organization/{departments|branches|designations}`.
- Recruitment: CRUD `/recruitment/{candidates|jobs}`, candidate stage action.
- Content: CRUD `/holidays`, `/announcements`, `/requests`, `/roles`. Employees may
  create their own requests; HR manages them. Holidays and announcements are readable
  by authenticated users.
- Settings: CRUD `/settings/{company|branches|departments|designations|shifts|leave|payroll|tax|notifications|email|security|audit}`.
  These are stored configuration records. Operational email/CORS/gateway settings are
  deployment configuration, not automatically loaded from this JSON store.
- Reports: HR `GET /reports/{summary|attendance|leave|payroll|employees}` and dashboard.
- Audit: HR `GET /audit?limit=100`. Records actor, operation, entity identity and request
  path in the same save as business changes. It excludes password/token/OTP values.

## Biometric gateway

`POST /biometric/devices/{id}/sync` requires `Biometric:GatewayUrl` (HTTPS) and optional
`Biometric:ApiKey`. The gateway must implement:

```text
GET {GatewayUrl}/devices/{escapedDeviceId}/attendance
Authorization: Bearer {ApiKey}
```

It must return a JSON array of `{ id, employeeId, date, checkIn, checkOut }` where dates
use `YYYY-MM-DD` and times use `HH:mm:ss`. IDs must be stable for deduplication. Sync
imports attendance and updates device status only after saving successfully. A failed
or missing gateway returns 502/503 and does not mark the device connected. Conflicting
existing attendance requires correction. This protocol needs a vendor-specific gateway;
no direct ZKTeco or other hardware integration has been verified.

## Validation

```powershell
dotnet test HRMS.Backend.slnx --no-restore
```

The integration suite runs real HTTP requests against isolated SQLite databases by default.
To run the same suite with real PostgreSQL migrations using API user-secrets:

```powershell
$env:HRMS_TEST_POSTGRES = '1'
dotnet test tests/HRMS.Api.IntegrationTests --no-restore
Remove-Item Env:HRMS_TEST_POSTGRES
```

This creates and drops only randomly named `hrms_test_<32 hex digits>` databases. The
configured PostgreSQL account needs CREATE DATABASE permission. Existing application
databases are not used for test data.

`/health/live` checks process availability; `/health/ready` checks the database.
Allowed frontend origins are configured in `Cors:Origins` (array).

## Completion boundaries

See [COMPLETION.md](COMPLETION.md) for remaining acceptance and deployment work.
The repository is not certified as 100% complete or production-ready merely because
its build and automated tests pass.
