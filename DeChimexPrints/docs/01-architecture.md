# 01 · Architecture & Flows

## 1. Solution architecture (Onion)

```mermaid
graph TD
    subgraph API["DeChimex.Host (Presentation)"]
        C[Controllers] --> MW[Middleware: Exception, Correlation, Security, RateLimit]
    end
    subgraph INFRA["DeChimex.Infrastructure"]
        JWT[TokenService/Hasher] ; PAY[PaystackGateway] ; MAIL[BrevoEmail] ; STORE[FileStorage] ; SEED[Seeder]
    end
    subgraph PERS["DeChimex.Persistence"]
        CTX[DbContext + Configs] ; REPO[Repositories] ; QRY[Query Services] ; UOW[UnitOfWork]
    end
    subgraph APP["DeChimex.Application"]
        CQRS[Commands/Queries + Handlers] ; VAL[Validators] ; BEH[Pipeline Behaviors] ; IFACE[Service Interfaces]
    end
    subgraph DOM["DeChimex.Domain (Core)"]
        AGG[Aggregates + Value Objects] ; EVT[Domain Events] ; RI[Repository Interfaces]
    end
    SH[DeChimex.Shared] ; CON[DeChimex.Contracts]

    API --> APP
    API --> INFRA
    API --> PERS
    INFRA --> APP
    PERS --> APP
    APP --> DOM
    APP --> CON
    DOM --> SH
    CON --> SH
```

**Dependency rule:** arrows point inward. Domain has no outward dependencies.
Infrastructure & Persistence implement interfaces declared in Application/Domain (Dependency Inversion).

---

## 2. Database ER diagram

```mermaid
erDiagram
    User ||--o{ UserRole : has
    Role ||--o{ UserRole : "assigned via"
    Role ||--o{ RolePermission : grants
    Permission ||--o{ RolePermission : "in"
    User ||--o{ RefreshToken : issues
    User ||--o| Customer : "linked to"

    Customer ||--o{ CustomerAddress : saves
    Customer ||--o{ Order : places
    Customer ||--o{ Payment : makes
    Customer ||--o{ WishlistItem : favourites
    Customer ||--o| ShoppingCart : owns
    Customer ||--o{ Review : writes

    Category ||--o{ Product : contains
    Product ||--o{ ProductImage : has
    Product ||--o| Inventory : "stocked by"
    Inventory ||--o{ InventoryTransaction : records

    Order ||--o{ OrderItem : "product lines"
    Order ||--o{ OrderServiceLine : "service lines"
    Order ||--o| Invoice : generates
    Order ||--o{ Payment : "paid by"
    Product ||--o{ OrderItem : "referenced in"
    Service ||--o{ OrderServiceLine : "referenced in"

    Procurement ||--o{ ProcurementItem : contains
    Order ||--o{ Procurement : "may trigger"

    ShoppingCart ||--o{ CartItem : contains
    GalleryMedia }|..|{ Category : "categorised (string)"
    Notification }o--o| User : "recipient (nullable)"
    AuditLog }o..o{ User : "performed by (string)"
    ApplicationSetting }|..|{ ApplicationSetting : "key-value"
```

### Entity relationship explanation
- **Security:** `User` ↔ `Role` (many-to-many via `UserRole`), `Role` ↔ `Permission` (via `RolePermission`).
  A `User` may be linked to one `Customer` profile. `RefreshToken` stores only a **hash**.
- **Catalog:** `Category 1-* Product 1-* ProductImage`. Each `Product` has exactly one `Inventory`
  (1-1), and every stock change appends an `InventoryTransaction` (immutable ledger).
- **Sales:** `Order` owns `OrderItem` (products) **and** `OrderServiceLine` (services). One `Invoice`
  per order (1-1); an order may have many `Payment` attempts. `OrderType` (Inventory/Service/Mixed)
  is **derived** from the lines present.
- **Procurement:** `Procurement 1-* ProcurementItem`, optionally linked to a source `Order` to fulfil
  a backorder. Receiving increases stock via the `Inventory` aggregate.
- **Customer experience:** `ShoppingCart 1-* CartItem`, `WishlistItem`, `Review`.
- **System:** `Notification` (nullable recipient = staff broadcast), `AuditLog`, `ApplicationSetting`.

---

## 3. Request flow (CQRS pipeline)

```mermaid
sequenceDiagram
    participant Client
    participant Controller
    participant MediatR
    participant Logging as LoggingBehavior
    participant Validation as ValidationBehavior
    participant Perf as PerformanceBehavior
    participant Handler
    participant Repo as Repository/QueryService
    participant UoW as UnitOfWork
    participant DB as SQL Server

    Client->>Controller: HTTP request (JWT)
    Controller->>MediatR: Send(Command/Query)
    MediatR->>Logging: pipeline
    Logging->>Validation: FluentValidation
    Validation->>Perf: stopwatch
    Perf->>Handler: Handle()
    Handler->>Repo: load aggregate / project read model
    Repo->>DB: query
    Handler->>UoW: SaveChangesAsync()
    UoW->>DB: commit
    UoW-->>Handler: dispatch domain events
    Handler-->>Controller: response DTO
    Controller-->>Client: ApiResponse<T> envelope
```

---

## 4. Authentication flow (JWT + refresh rotation)

```mermaid
sequenceDiagram
    participant Client
    participant Api
    participant TokenService
    participant DB

    Client->>Api: POST /auth/login (email, password)
    Api->>DB: load user + roles
    Api->>Api: verify password (PBKDF2), check lockout/active
    Api->>TokenService: create access (JWT) + refresh token
    TokenService-->>Api: access (15m) + refresh (7d, hash stored)
    Api-->>Client: AuthResponse (accessToken, refreshToken)

    Note over Client,Api: access token expires
    Client->>Api: POST /auth/refresh (refreshToken)
    Api->>DB: find by token hash
    Api->>Api: validate active; ROTATE (revoke old, issue new)
    Api-->>Client: new access + refresh pair
    Note over Api: reuse of revoked token -> revoke ALL sessions
```

Access tokens carry `role` and `permission` claims; endpoints use
`[Authorize(Policy = Permissions.X)]` resolved by a dynamic `PermissionPolicyProvider`.

---

## 5. Payment flow (Paystack): inventory deducted only after payment

```mermaid
sequenceDiagram
    participant Client
    participant Api
    participant Paystack
    participant Handler
    participant Inventory
    participant Invoice

    Client->>Api: POST /payments/initialize (orderId, method)
    Api->>Paystack: initialize transaction
    Paystack-->>Api: authorization_url + reference
    Api-->>Client: authorization_url
    Client->>Paystack: pay on hosted page
    Paystack-->>Api: webhook charge.success (signed)
    Api->>Api: verify HMAC-SHA512 signature
    Api->>Handler: confirm (idempotent)
    Handler->>Handler: Order.MarkPaid()
    Handler->>Inventory: reserve stock (products only; shortage -> AwaitingProcurement)
    Handler->>Invoice: issue + mark paid
    Handler-->>Api: done
    Note over Client,Api: GET /payments/verify also confirms (idempotent)
```

---

## 6. Email flow (Brevo, via domain events)

```mermaid
sequenceDiagram
    participant Handler
    participant UoW
    participant EventHandler
    participant Queue as EmailQueue
    participant Brevo

    Handler->>UoW: SaveChangesAsync()
    UoW-->>EventHandler: publish UserRegistered / OrderPaid / PasswordResetRequested ...
    EventHandler->>EventHandler: render branded template
    EventHandler->>Queue: EnqueueAsync(EmailMessage)
    Queue->>Brevo: POST /smtp/email (background scope)
    Brevo-->>Queue: 201 Created
```

Templates: Welcome, EmailVerification, ForgotPassword, PasswordResetConfirmation, Otp,
OrderConfirmation, OrderStatusUpdate, PaymentReceipt.

---

## 7. Order lifecycle state machine

```mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> PendingPayment: PlaceOrder
    PendingPayment --> Paid: payment confirmed
    Paid --> AwaitingProcurement: stock shortage
    Paid --> InProduction: StartProduction (stock deducted)
    AwaitingProcurement --> InProduction: stock received
    InProduction --> Ready: MarkReady
    Ready --> Completed: MarkCompleted
    Completed --> Delivered: MarkDelivered
    PendingPayment --> Cancelled
    Paid --> Refunded
    InProduction --> Refunded
```
