# 10 · Frontend Integration Guide

The existing HTML/JS frontend (and a future React/Next.js app) integrates against `/api/v1`.

## Base API configuration
```js
const API = "https://api.dechimexprinthub.com/api/v1"; // or http://localhost:5000/api/v1 in dev
```
All responses use the envelope `{ success, message, data, errors, errorCode, traceId }`.
Always read `data` on success and `errors`/`message` on failure.

## Authentication flow
```js
async function login(email, password) {
  const res = await fetch(`${API}/auth/login`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password })
  });
  const body = await res.json();
  if (!body.success) throw new Error(body.message);
  saveTokens(body.data); // { accessToken, refreshToken, accessTokenExpiresUtc, ... }
  return body.data;
}
function saveTokens(d) {
  localStorage.setItem("accessToken", d.accessToken);
  localStorage.setItem("refreshToken", d.refreshToken);
  localStorage.setItem("accessExpires", d.accessTokenExpiresUtc);
}
```

## Authenticated requests + refresh-token handling
```js
async function apiFetch(path, options = {}, retry = true) {
  const token = localStorage.getItem("accessToken");
  const res = await fetch(`${API}${path}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options.headers||{}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}) }
  });
  if (res.status === 401 && retry) {
    if (await tryRefresh()) return apiFetch(path, options, false); // retry once
  }
  return res.json();
}
async function tryRefresh() {
  const refreshToken = localStorage.getItem("refreshToken");
  if (!refreshToken) return false;
  const res = await fetch(`${API}/auth/refresh`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ refreshToken })
  });
  const body = await res.json();
  if (body.success) { saveTokens(body.data); return true; }
  localStorage.clear(); location.href = "/login.html"; return false;
}
```
> Refresh tokens are **rotated** on every use. Always store the new one from the response.

## Error handling conventions
| HTTP | Meaning | Frontend action |
|---|---|---|
| 400 | Validation (`errors` map keyed by field) | Show field errors |
| 401 | Unauthorized/expired | Refresh once, else redirect to login |
| 403 | Forbidden (missing permission) | Show "no access" |
| 404 | Not found | Show empty/not-found state |
| 409 | Conflict (e.g. duplicate SKU/email) | Show `message` |
| 422 | Business rule violation | Show `message` |
| 429 | Rate limited | Back off & retry |
| 500 | Server error (with `traceId`) | Generic error + include `traceId` in support |

## Pagination
List endpoints return `data = { items, page, pageSize, totalRecords, totalPages, hasPrevious, hasNext }`.
Send `?page=&pageSize=&search=&sortBy=&sortDescending=` (plus filters like `categoryId`, `status`).

## File upload
```js
const fd = new FormData();
fd.append("file", fileInput.files[0]);
fd.append("category", "ProductImage"); // ProductImage | GalleryMedia | InvoiceDocument | Document
const res = await fetch(`${API}/files/upload`, {
  method: "POST", headers: { Authorization: `Bearer ${token}` }, body: fd });
const { data } = await res.json(); // { url, storedFileName, size }
```

## Paystack payment integration
```js
// 1. Initialize
const { data } = await apiFetch(`/payments/initialize`, {
  method: "POST", body: JSON.stringify({ orderId, method: "Paystack" }) });
// 2. Redirect the customer to Paystack
window.location.href = data.authorizationUrl;
// 3. On return, verify (or rely on the server webhook)
await apiFetch(`/payments/verify`, { method: "POST", body: JSON.stringify({ reference: data.reference }) });
```

## Email verification flow
1. After register, the user receives a link → `FrontendBaseUrl/verify-email?email=&token=`.
2. Your page reads the query params and calls:
```js
await fetch(`${API}/auth/verify-email`, { method:"POST",
  headers:{ "Content-Type":"application/json" },
  body: JSON.stringify({ email, token }) });
```

## Password reset flow
1. `POST /auth/forgot-password { email }` (always returns success).
2. User clicks emailed link → `FrontendBaseUrl/reset-password?email=&token=`.
3. `POST /auth/reset-password { email, token, newPassword }`.

## Mapping existing frontend pages → endpoints
| Frontend page | Endpoint(s) |
|---|---|
| products.html | `GET /products` (+filters), `GET /categories` |
| product.html | `GET /products/{id}` |
| services.html | `GET /services` |
| gallery.html | `GET /gallery` |
| cart.html | `GET/POST/PUT/DELETE /cart` |
| checkout.html | `POST /orders`, `POST /payments/initialize` |
| track.html | `GET /orders/track/{orderNumber}` |
| invoice.html | `GET /payments/invoice/{orderId}` |
| login/register | `/auth/login`, `/auth/register` |
| dashboard-customer.html | `/orders/mine`, `/customers/me`, `/wishlist`, `/payments/history` |
| dashboard-admin.html | `/dashboard/kpis`, `/orders`, `/inventory`, `/inventory/low-stock`, `/procurement`, `/notifications` |
