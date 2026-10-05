# 02 · API Endpoint Reference

Base URL: `/api/v1`. All responses use the envelope:

```json
{ "success": true, "message": "...", "data": { }, "errors": null, "errorCode": null, "traceId": "..." }
```

Auth: send `Authorization: Bearer <accessToken>`. `[Public]` = no auth. `[Perm: x]` = requires permission `x`.

## Auth
| Method | Route | Access | Description |
|---|---|---|---|
| POST | `/auth/register` | Public | Register customer, returns tokens |
| POST | `/auth/login` | Public | Login, returns tokens |
| POST | `/auth/refresh` | Public | Rotate refresh token |
| POST | `/auth/verify-email` | Public | Verify email with token |
| POST | `/auth/forgot-password` | Public | Request reset link |
| POST | `/auth/reset-password` | Public | Reset with token |
| POST | `/auth/change-password` | Auth | Change own password |

## Products
| Method | Route | Access |
|---|---|---|
| GET | `/products` (search, filter, sort, paginate) | Public |
| GET | `/products/featured?take=8` | Public |
| GET | `/products/{id}` | Public |
| POST | `/products` | Perm: products.create |
| PUT | `/products/{id}` | Perm: products.update |
| PATCH | `/products/{id}/active?isActive=` | Perm: products.update |
| DELETE | `/products/{id}` | Perm: products.delete |
| POST | `/products/{id}/images` | Perm: products.update |
| DELETE | `/products/{id}/images/{imageId}` | Perm: products.update |

**Product query params:** `page, pageSize, search, sortBy(price|name), sortDescending, categoryId, isFeatured, activeOnly, minPrice, maxPrice`.

## Categories
`GET /categories` (public), `GET /categories/{id}` (public), `POST/PUT/DELETE` (Perm: categories.manage).

## Services
`GET /services`, `GET /services/{id}` (public); `POST/PUT/DELETE` (Perm: services.manage).

## Inventory (Perm: inventory.view)
| Method | Route | Access |
|---|---|---|
| GET | `/inventory` (paged) | inventory.view |
| GET | `/inventory/low-stock` | inventory.view |
| GET | `/inventory/{productId}/history` | inventory.view |
| POST | `/inventory/{productId}/stock-in` | inventory.stockin |
| POST | `/inventory/{productId}/adjust` | inventory.adjust |
| PATCH | `/inventory/{productId}/threshold` | inventory.adjust |

## Orders (Auth)
| Method | Route | Access |
|---|---|---|
| GET | `/orders` (search/filter) | orders.viewall |
| GET | `/orders/mine` | Auth (customer) |
| GET | `/orders/{id}` | Auth |
| GET | `/orders/track/{orderNumber}` | Public |
| POST | `/orders` | orders.create |
| PATCH | `/orders/{id}/status` | orders.updatestatus |
| POST | `/orders/{id}/cancel` | orders.cancel |
| POST | `/orders/{id}/refund` | orders.refund |

## Payments
| Method | Route | Access |
|---|---|---|
| POST | `/payments/initialize` | Auth |
| POST | `/payments/verify` | Auth |
| GET | `/payments/callback?reference=` | Public |
| POST | `/payments/webhook/paystack` | Public (signature-verified) |
| POST | `/payments/{paymentId}/confirm-manual` | orders.updatestatus |
| GET | `/payments/history` | Auth (self) |
| GET | `/payments/invoice/{orderId}` | Auth |

## Customers (Auth)
`GET /customers` (customers.view, paged), `GET /customers/{id}` (customers.view),
`GET/PUT /customers/me`, `POST /customers/me/addresses`, `DELETE /customers/me/addresses/{addressId}`.

## Cart / Wishlist (Auth, self)
`GET /cart`, `POST /cart/items`, `PUT /cart/items/{itemId}`, `DELETE /cart/items/{itemId}`, `DELETE /cart`.
`GET /wishlist`, `POST /wishlist/items`, `DELETE /wishlist/items/{productId}`.

## Gallery
`GET /gallery`, `GET /gallery/homepage` (public); `POST/PUT/DELETE`, `PATCH /gallery/{id}/archive` (Perm: gallery.manage).

## Procurement (Perm: procurement.view)
`GET /procurement` (paged), `GET /procurement/{id}`, `POST /procurement` (approve),
`POST /procurement/{id}/approve|purchase|receive`.

## Users & Roles (Perm: users.view)
`GET /users` (paged), `GET /users/roles`, `GET /users/permissions`,
`POST /users` (users.create), `PATCH /users/{id}/active` (users.disable), `PUT /users/{id}/roles` (users.assignroles).

## Dashboard (Perm: reports.view)
`GET /dashboard/kpis`, `GET /dashboard/revenue-trend?months=8`, `GET /dashboard/sales-by-category`.

## Notifications (Auth)
`GET /notifications?unreadOnly=`, `PATCH /notifications/{id}/read`,
`GET /notifications/unread-count` (MVC-only helper for the sidebar badge).
Since the 2026-08-29 scoping change, staff notifications are filtered by the
caller's permission claims (each notification carries a RequiredPermission:
NewOrder = orders.view, PaymentReceived/OrderCompleted = orders.updatestatus,
LowStock = inventory.view, ProcurementRequired = procurement.view; null =
legacy all-staff row), and mark-read is rejected with 404 for rows the caller
cannot see. New notifications are pushed live to authenticated clients over
the SignalR hub at `POST /hubs/notifications/negotiate` + WebSocket
("notification" event).

## Files (Auth)
`POST /files/upload` (multipart: `file`, `category` = ProductImage|GalleryMedia|InvoiceDocument|Document).

## Health
`GET /health/live`, `GET /health/ready`.
