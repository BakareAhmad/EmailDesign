# 06 · Paystack (Payments) Setup

## 1. Get API keys
1. Create an account at https://dashboard.paystack.com.
2. **Settings → API Keys & Webhooks**: copy your **Secret Key** and **Public Key**
   (use `sk_test_…` / `pk_test_…` in staging, live keys in production).

## 2. Configure
```json
"Paystack": {
  "SecretKey": "sk_test_xxx",
  "PublicKey": "pk_test_xxx",
  "ApiBaseUrl": "https://api.paystack.co",
  "Currency": "NGN"
}
```
Env-var form: `Paystack__SecretKey`, `Paystack__PublicKey`.

## 3. Configure the webhook
In **Settings → API Keys & Webhooks**, set the webhook URL to:
```
https://api.dechimexprinthub.com/api/v1/payments/webhook/paystack
```
Paystack signs each webhook with **HMAC-SHA512** using your secret key. The API verifies the
`x-paystack-signature` header **before** trusting any payload. Invalid signatures are ignored.

## 4. Payment lifecycle
```
POST /payments/initialize  -> creates Payment (Initialized), returns authorization_url
Customer pays on Paystack hosted page
Paystack -> webhook charge.success (signed)  OR  client -> GET /payments/verify
API verifies -> Payment.MarkSucceeded (idempotent)
             -> Order.MarkPaid -> reserve inventory (products only; shortage => AwaitingProcurement)
             -> Invoice issued & marked paid -> customer lifetime spend updated -> receipt email
```

## 5. Idempotency & safety
- **Duplicate webhooks / repeated verify calls are safe**. A succeeded payment short-circuits.
- Amounts are converted to **kobo** (minor unit) for Paystack and back for storage.
- Refunds: `POST /orders/{id}/refund` calls the gateway `/refund` for gateway payments and
  marks manual payments refunded locally.

## 6. Non-gateway methods
Counter `BankTransfer` and `Cash` payments are confirmed by staff ("mark as paid"),
which records a Succeeded payment and runs the shared post-payment fulfilment rules.
The API also exposes `POST /payments/{paymentId}/confirm-manual` to confirm any
existing pending payment the same way. There is no pay-on-delivery option.

## 7. Test cards
Use Paystack test cards (e.g. `4084 0840 8408 4081`, any future expiry, any CVV, OTP `123456`)
in test mode. See https://paystack.com/docs/payments/test-payments.
