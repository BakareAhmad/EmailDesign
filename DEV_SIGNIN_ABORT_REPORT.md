# Developer Portal sign-in — "OperationCanceledException" in Visual Studio (2026-09-26)

## What was reported

Signing in to the Developer Portal locally, Visual Studio stopped with
**Exception User-Unhandled — System.OperationCanceledException: 'The operation was canceled.'**
on `ServiceFeeReportService.OverviewAsync` (the `ApplicationSettings` query, line 146), called by the dashboard.

## Root cause

Nothing crashed, and no data was wrong. That exception means **the browser dropped the request while
the dashboard was still loading**. Every Developer Portal query is tied to `HttpContext.RequestAborted`,
so when the browser goes away, the database query stops and EF throws `OperationCanceledException`.
In a local debug session the first dashboard load is slow (cold start, the debugger, SQL Server, live
Paystack/Brevo health checks), so it's easy to trigger by:

- clicking **Sign in** a second time, or pressing Enter again;
- reloading, or clicking a nav link, while `/dev` is still loading;
- the dashboard's 30-second status poll (`/dev/api/status`) still running when you leave the page.

The app already turned it into a harmless 499 in the global `ExceptionHandlingMiddleware`. But the exception
had to leave *your* code through ASP.NET MVC first, and with *Just My Code* on, Visual Studio reports that
as "User-Unhandled" and breaks. Each aborted load also wrote a warning to the log.

I reproduced it in the sandbox with a real HTTP client that resets the connection mid-request. The log
showed `Handled OperationCanceledException on GET /dev/api/status`, which is the same path as the report.

## Fix

`DevControllerBase` (all Developer Portal controllers inherit it):

- **New `UnlessClientAborted(...)` wrapper.** It catches `OperationCanceledException` **only when
  `RequestAborted` is signalled**, i.e. the browser really left. Then it returns a quiet
  **499 "Client Closed Request"** with no body and no log noise. Any other cancellation (for example a real
  internal timeout) and any other exception still propagate exactly as before.
- It's applied to all **17 read-only Developer Portal pages**: dashboard, status JSON, health, performance,
  database, logs, log detail, error center, error group, integrations, payments, email, service fees,
  ledger, fee detail, CSV export and maintenance list. The maintenance POST (a write) is untouched.
- The live permission re-check that runs before every dev action gets the same guard.
- Status-code pages are switched off for that one 499 response. Otherwise the site's "never a blank page"
  net would re-run the request through `/Error` for a browser that has already gone.

## A second bug found while verifying (fixed)

The site's hand-rolled status-code re-execute (`Program.cs`) is meant to turn any empty error response
into a styled error page. It **didn't clear the endpoint the first pass had picked**. `UseRouting` skips
matching when an endpoint is already set, so the re-executed request ran **the original action a second
time** instead of the error page:

- `/admin/customers/<unknown id>` and `/admin/customers/<unknown id>/edit` (any action that returns
  `NotFound()`) showed a **blank 404 page**;
- a form posted with a missing or expired anti-forgery token showed a **blank 400 page**.

Fix: the endpoint and route values are cleared before re-executing, the same way ASP.NET's built-in
re-execute does it. The error page is then fetched as **GET**, so a rejected POST reaches the GET-only
`ErrorController` instead of a 405. `ErrorController` keeps the original status (400, 404…), so API/JSON
behaviour and status codes are unchanged. Unknown URLs worked before and still do.

## If you still see the break in Visual Studio

With this fix the dashboard and the other dev pages no longer let the exception escape your code, so the
debugger stays quiet. If you ever see it again from *another* page, it's the same harmless
"browser went away" case. Press **Continue (F5)**, or untick *Break when this exception type is
user-unhandled* for `System.OperationCanceledException` in the exception popup's **Exception Settings**.

## Tests

- `IntegrationTests/DevPortal/DevPortalClientAbortTests.cs` (8 tests):
  - a client abort mid-action becomes a 499;
  - `TaskCanceledException` is handled too;
  - a non-client cancellation **still throws**;
  - other exceptions are untouched;
  - the whole pipeline with an aborted `/dev`, `/dev/api/status` and `/dev/service-fees` gives a 499 with
    no error page, while the same live request gives 200;
  - normal sign-in renders the dashboard.
  - Proven to **fail on the old code** (5 of 8 red) and pass on the new.
- `IntegrationTests/DevPortal/StatusCodeReExecuteTests.cs` (4 tests):
  - missing customer details and edit give the styled 404 with the staff home link;
  - a browser POST without an anti-forgery token gives the styled 400 (not blank, not 405);
  - unknown URL and `/Error?code=400` still render.

## End-to-end proof (`e2e/dev-abort/dev_abort_e2e.py`, 37 checks)

Real Chromium plus raw sockets against the running app:

- sign-in validation (empty fields, wrong password) and correct sign-in to the dashboard;
- double-submit plus reload during sign-in;
- an **abort storm**: 120 dev requests reset mid-flight across 8 pages. The app log shows they really were
  cut mid-request (499 on `/dev`, `/dev/api/status`, `/dev/service-fees`…) with **zero** cancellations
  reaching the global handler, zero stack traces, zero `/Error?code=499` re-executions and zero
  unhandled exceptions;
- browser-aborted status polls;
- health, dashboard, status JSON and service fees all normal afterwards;
- the blank-404/400 fixes in the browser, including checking that the action ran once and the rejected
  form created nothing.

## Final verified numbers (one clean run: `bash e2e/dev-abort/run_final.sh`)

| Stage | Result |
|---|---|
| Full test suite | 879/879 (459 unit + 420 integration) |
| Public `/health` | Healthy |
| Developer smoke | 29/29 |
| Checkout regression | 60/60 |
| Brand images + responsive (regression) | 245/245 |
| Design-review regression | 36/36 |
| Fixes journey (regression) | 39/39 |
| Developer-portal regression | 107/107 |
| **Dev sign-in + dropped requests + blank-404/400** | **37/37** |
| App unhandled exceptions / secret leaks | 0 / clean |

The integration suite took 5 min 13 s in that run, against its usual ~1 min. A re-run of the same build
straight afterwards took 58 s, and no single test takes more than 5 s, so it was a one-off sandbox slowdown.

Evidence: `evidence/` (10 screenshots plus every stage log; index in `evidence/README.md`). The previous
brand-images run is preserved unchanged in `e2e/brand/evidence-final-run/`.
