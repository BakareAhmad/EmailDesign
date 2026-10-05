# 11 · NuGet Packages & Testing

## Required NuGet packages (by project)

**Domain**
- MediatR.Contracts (domain-event marker only, which keeps the domain pure)

**Application**
- MediatR
- FluentValidation.DependencyInjectionExtensions
- AutoMapper
- Microsoft.Extensions.DependencyInjection.Abstractions
- Microsoft.Extensions.Logging.Abstractions

**Persistence**
- Microsoft.EntityFrameworkCore (+ .Relational)
- Microsoft.EntityFrameworkCore.SqlServer

**Infrastructure**
- Microsoft.EntityFrameworkCore
- System.IdentityModel.Tokens.Jwt, Microsoft.IdentityModel.Tokens
- Microsoft.AspNetCore.Authentication.JwtBearer
- Microsoft.AspNetCore.Http.Abstractions
- Microsoft.Extensions.Options.ConfigurationExtensions
- Microsoft.Extensions.Http
- Microsoft.Extensions.Caching.Memory

**Api**
- Serilog.AspNetCore (+ Sinks.Console, Sinks.File, Enrichers.Environment)
- MediatR, FluentValidation.AspNetCore
- Microsoft.EntityFrameworkCore.Design
- Asp.Versioning.Mvc (+ .ApiExplorer)
- Swashbuckle.AspNetCore
- Microsoft.AspNetCore.Authentication.JwtBearer
- AspNetCore.HealthChecks.SqlServer
- Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore

**Tooling:** `dotnet-ef` 9.0.11 (pinned in `.config/dotnet-tools.json`).

## Testing readiness

The solution is structured for testability:
- **Dependency inversion everywhere**. Handlers depend on interfaces (`IRepository`, `IPaymentGateway`,
  `IEmailService`, `ITokenService`, `ICurrentUser`, `IDateTimeProvider`, query services), all mockable.
- **Domain is pure**. Aggregate behaviour (order state machine, inventory rules, money math) is unit-testable
  with no infrastructure.
- **`Program` is `public partial`**. Integration tests can use `WebApplicationFactory<Program>`.

### Suggested test projects
```
tests/
├── DeChimex.Domain.UnitTests          # aggregates, value objects, invariants
├── DeChimex.Application.UnitTests      # handlers with mocked interfaces (Moq/NSubstitute + FluentAssertions)
└── DeChimex.Host.IntegrationTests       # WebApplicationFactory + Testcontainers (SQL Server) or EF InMemory
```

### Example unit test (domain)
```csharp
[Fact]
public void Reserve_WhenStockShort_ReturnsShortage_AndRaisesEvent()
{
    var inv = Inventory.Create(productId: Guid.NewGuid(), "Mug", initialQuantity: 2, lowStockThreshold: 5);
    var shortage = inv.Reserve(quantity: 5, "ORD-1", "test");
    shortage.Should().Be(3);
    inv.DomainEvents.Should().ContainSingle(e => e is InventoryShortageDetectedEvent);
}
```

### API testing checklist
- [ ] Register → verify email → login → refresh → change password.
- [ ] Product CRUD honours permissions (403 without the right claim).
- [ ] Create order with zero stock succeeds (not blocked); flags procurement after payment.
- [ ] Payment verify is idempotent (call twice → single confirmation, stock deducted once).
- [ ] Webhook rejects bad signature; accepts valid signed body.
- [ ] Service-only order never changes inventory.
- [ ] Pagination metadata correct; validation returns field-keyed errors.
- [ ] Health endpoints return 200; rate limiting returns 429 past the threshold.
