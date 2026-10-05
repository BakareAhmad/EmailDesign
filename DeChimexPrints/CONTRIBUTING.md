# Contributing to DE-CHIMEX PRINT HUB

Thanks for your interest in contributing! This guide explains how to work on the codebase
consistently.

## Getting started

1. Install the **.NET 9 SDK** and a local **SQL Server** (or Docker). See
   [`SETUP.md`](SETUP.md) and [`docs/07-configuration.md`](docs/07-configuration.md).
2. Configure secrets (at minimum `ConnectionStrings:DefaultConnection` and a `Jwt:SigningKey`
   of ≥ 32 characters).
3. Restore tools and run:
   ```bash
   dotnet tool restore
   dotnet run --project src/DeChimex.Host
   ```

## Content and copy

All user-facing text (buttons, headings, empty states, errors, emails, toasts)
follows the project's **Human UI Writing Standard**: professional, natural,
context-specific copy with no em/en dashes, no AI-style phrasing and no generic
SaaS filler. The full rule list lives in [`../AI_HANDOFF.txt`](../AI_HANDOFF.txt)
("UI CONTENT AND WRITING STANDARD") and is a permanent project constraint.

## Code style

- Style is enforced by the root [`.editorconfig`](../.editorconfig): **PascalCase** for C#
  public members, **_camelCase** for private fields, **camelCase** for JS, 4-space C# /
  2-space JS indent.
- Follow the existing **Onion / Clean Architecture** layering - dependencies point inward
  (see [`docs/01-architecture.md`](docs/01-architecture.md)).
- Keep mapping/validation in the **Application** layer (FluentValidation), persistence in
  **Persistence**, and external integrations in **Infrastructure**.

## Workflow

1. Create a feature branch from `main`: `feature/<short-description>`.
2. Make focused commits with clear messages (imperative mood, e.g. "Add order refund guard").
3. **Build and test before pushing:**
   ```bash
   dotnet build DeChimex.sln
   dotnet test tests/DeChimex.UnitTests
   dotnet test tests/DeChimex.IntegrationTests
   ```
4. Open a pull request describing the change, the problem it solves, and any test/verification
   steps. Link the relevant issue/ID (e.g. `MED-004`).

## Testing expectations

- New business logic in the domain/application layer should have **unit tests**.
- New/changed endpoints should have **integration tests** (via `CustomWebApplicationFactory`,
  which runs the API on an EF InMemory database).
- Never commit secrets or `appsettings.Production.json` (it is git-ignored).

## Definition of done

- `dotnet build` succeeds with **0 warnings / 0 errors**.
- All unit and integration tests pass.
- Public APIs carry XML doc comments; user-facing behaviour is documented where relevant.
- No regressions to existing behaviour (or they are explicitly called out).
