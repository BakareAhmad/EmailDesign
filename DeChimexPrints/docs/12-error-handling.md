# Error Handling & Error Codes (MED-006)

All API errors are returned in a single, consistent envelope (`ApiResponse`) so clients can
handle failures uniformly. Errors are produced centrally by `ExceptionHandlingMiddleware` -
controllers and handlers throw typed exceptions and never build error payloads by hand.

## Response envelope

```json
{
  "success": false,
  "message": "A category named 'Mugs' already exists.",
  "errorCode": "category.exists",
  "errors": null,
  "traceId": "0HMV..."
}
```

| Field       | Type                  | Meaning                                                        |
|-------------|-----------------------|---------------------------------------------------------------|
| `success`   | `bool`                | `false` for every error response.                              |
| `message`   | `string`              | Human-readable summary (safe to show to end users).            |
| `errorCode` | `string?`             | Stable machine-readable code for programmatic handling.        |
| `errors`    | `map<string,string[]>?` | Field-level validation errors (only for `validation.failed`). |
| `traceId`   | `string?`             | Correlation/trace id (also returned in `X-Correlation-Id`).    |

## HTTP status mapping

| Exception                       | Status | errorCode (default)            |
|---------------------------------|--------|--------------------------------|
| `ValidationAppException`        | 400    | `validation.failed` (+ `errors`) |
| `UnauthorizedAppException`      | 401    | `unauthorized`                 |
| `ForbiddenException`            | 403    | `forbidden`                    |
| `NotFoundException`             | 404    | exception `Code` (e.g. `notfound`) |
| `ConflictException`             | 409    | exception `Code` (e.g. `category.exists`, `auth.email.exists`) |
| `BusinessRuleValidationException` | 422  | exception `Code`               |
| `DomainException`               | 422    | exception `Code`               |
| `OperationCanceledException`    | 499    | `request.cancelled`            |
| any other                       | 500    | `server.error`                 |

## Error-code conventions

- Use **lowercase, dot-separated** codes: `<area>.<reason>` (e.g. `category.exists`,
  `auth.email.exists`, `order.already.paid`).
- Reserve these global codes: `validation.failed`, `unauthorized`, `forbidden`,
  `request.cancelled`, `server.error`.
- Codes are **stable** (clients may branch on them); messages are **not** (they may change
  wording). Add new codes rather than reusing existing ones for different meanings.

## Security & logging

- Stack traces are **never** returned to clients; the 500 message is generic.
- 5xx errors are logged at `Error` with the exception; 4xx at `Warning`. Every log line
  includes the correlation id (via Serilog `LogContext`) plus the request method, path and
  user (MED-005).

## Frontend handling

The frontend `http()` helper already normalises responses: it returns the envelope when
`success` is a boolean, and otherwise wraps unexpected shapes. UI code should check
`result.success`, surface `result.message`, and use `result.errors` to annotate fields.
