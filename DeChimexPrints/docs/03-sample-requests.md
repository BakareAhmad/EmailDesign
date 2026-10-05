# 03 · Sample Requests & Responses

### Register
```http
POST /api/v1/auth/register
Content-Type: application/json

{ "firstName":"Amaka","lastName":"Obi","email":"amaka@zenith.com","phone":"+2348020001234","password":"Passw0rd!" }
```
```json
{ "success": true, "message": "Registration successful. Please verify your email.",
  "data": { "userId":"...","email":"amaka@zenith.com","fullName":"Amaka Obi",
    "accessToken":"eyJ...","accessTokenExpiresUtc":"2026-07-17T16:45:00Z",
    "refreshToken":"abc...","refreshTokenExpiresUtc":"2026-07-24T16:30:00Z",
    "roles":["Customer"],"permissions":[] } }
```

### Login
```http
POST /api/v1/auth/login
{ "email":"owner@dechimexprinthub.com", "password":"ChangeMe123!" }
```

### Refresh
```http
POST /api/v1/auth/refresh
{ "refreshToken":"abc..." }
```

### Search products
```http
GET /api/v1/products?search=mug&categoryId=<guid>&sortBy=price&page=1&pageSize=12
```
```json
{ "success": true, "data": {
  "items":[{ "id":"...","name":"Magic Color-Changing Mug","sku":"MUG-001","sellingPrice":4500,
    "currency":"NGN","categoryName":"Gift Items","availableQuantity":120,"isLowStock":false,
    "isOutOfStock":false,"primaryImageUrl":null }],
  "page":1,"pageSize":12,"totalRecords":1,"totalPages":1,"hasPrevious":false,"hasNext":false } }
```

### Create product (admin)
```http
POST /api/v1/products
Authorization: Bearer <token>

{ "name":"Ceramic Photo Mug","sku":"MUG-018","description":"Full-wrap photo print.",
  "categoryId":"<guid>","sellingPrice":4000,"isCustomizable":true,"isFeatured":false,
  "initialQuantity":96,"lowStockThreshold":20 }
```

### Create a mixed order (products + service; not blocked by stock)
```http
POST /api/v1/orders
Authorization: Bearer <token>

{ "customerId":"<guid>",
  "items":[{ "productId":"<mugId>","quantity":3 }],
  "services":[{ "serviceId":"<engravingId>","quantity":1,"notes":"Logo on customer flask" }],
  "shippingAddress":{ "street":"12 Adeola Odeku","city":"Lagos","state":"Lagos","country":"Nigeria","postalCode":"100001" },
  "billingAddress":null, "customerNotes":"Deliver before Friday",
  "discount":0, "deliveryFee":1500, "tax":0 }
```

### Initialize Paystack payment
```http
POST /api/v1/payments/initialize
{ "orderId":"<guid>", "method":"Paystack" }
```
```json
{ "success":true, "data":{ "paymentId":"...","reference":"PAY-...","authorizationUrl":"https://checkout.paystack.com/xyz","status":"Initialized" } }
```

### Paystack webhook (server-to-server)
```http
POST /api/v1/payments/webhook/paystack
x-paystack-signature: <hmac-sha512-hex>

{ "event":"charge.success", "data":{ "reference":"PAY-...","amount":3100000,"status":"success" } }
```

### Stock-in
```http
POST /api/v1/inventory/<productId>/stock-in
{ "quantity":100, "reference":"PRC-2026-000001", "notes":"Procurement received" }
```

### Validation error shape
```json
{ "success": false, "message": "One or more validation errors occurred.",
  "errorCode": "validation.failed",
  "errors": { "Request.SellingPrice": ["'Selling Price' must be greater than or equal to '0'."] },
  "traceId": "0HN..." }
```

### Not found / conflict
```json
{ "success": false, "message": "Product with key '...' was not found.", "errorCode": "product.notfound", "traceId": "..." }
{ "success": false, "message": "SKU 'MUG-001' is already in use.", "errorCode": "product.sku.exists", "traceId": "..." }
```
