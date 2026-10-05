# API Versioning Strategy (MED-008)

## Current version

The API is at **v1**. All routes are versioned in the URL path:

```
https://api.dechimexprinthub.com/api/v1/<controller>
```

Versioning is provided by `Asp.Versioning` (see `AddApiVersioningConfigured`):

- `DefaultApiVersion = 1.0`
- `AssumeDefaultVersionWhenUnspecified = true`
- `ApiVersionReader = UrlSegmentApiVersionReader` (the version lives in the `v{version}` segment)
- `ReportApiVersions = true` → responses include the `api-supported-versions` header
- Controllers declare `[ApiVersion("1.0")]` and route via `api/v{version:apiVersion}/[controller]`.

## Version headers

Because `ReportApiVersions` is enabled, responses advertise supported versions:

```
api-supported-versions: 1.0
```

When a deprecated version is served, `api-deprecated-versions` is also emitted. Clients
should treat these as informational; the URL segment is the source of truth.

## Deprecation policy

1. **Introduce** the new version side-by-side (e.g. `v2`) without removing `v1`.
2. **Announce** deprecation at least **6 months** before sunset; add the
   `api-deprecated-versions` header and, where possible, a `Sunset`/`Deprecation` header on
   the old version.
3. **Migrate** clients within the window. Track usage per version before removal.
4. **Sunset** the old version only after usage is negligible; return `410 Gone` with a
   pointer to the migration guide thereafter.

Breaking changes that **require** a new version: removing/renaming fields or endpoints,
changing field types/semantics, tightening auth. Additive changes (new optional fields, new
endpoints) do **not** require a new version.

## Migration guide template

When shipping `v(N+1)`, publish `docs/migrations/vN-to-vN+1.md` containing:

1. **Overview**: why the new version exists; support timeline for the old version.
2. **Breaking changes**: table of `old → new` (endpoint, request, response).
3. **Step-by-step**: ordered changes a client must make.
4. **Examples**: before/after request & response payloads.
5. **FAQ / rollback**: common issues and how to fall back.
