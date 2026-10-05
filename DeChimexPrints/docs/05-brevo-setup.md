# 05 · Brevo (Email) Setup

DE-CHIMEX sends transactional email through the **Brevo (formerly Sendinblue) v3 API**.

## 1. Create credentials
1. Sign up / log in at https://app.brevo.com.
2. **Senders & IP → Senders**: add and verify your sender email (e.g. `no-reply@dechimexprinthub.com`).
   For best deliverability, authenticate your domain (SPF, DKIM).
3. **SMTP & API → API Keys**: create an API key (v3).

## 2. Configure
`appsettings.json` / environment / secrets:
```json
"Brevo": {
  "ApiKey": "<your-brevo-v3-api-key>",
  "ApiBaseUrl": "https://api.brevo.com/v3",
  "SenderName": "DE-CHIMEX PRINT HUB",
  "SenderEmail": "no-reply@dechimexprinthub.com",
  "Enabled": true
}
```
Env-var form: `Brevo__ApiKey`, `Brevo__SenderEmail`, `Brevo__Enabled`.

> When `Enabled=false` or the API key is empty, emails are **logged and skipped**, which is ideal for
> local dev without credentials. No code changes needed to toggle.

## 3. How it works
- Domain events (`UserRegistered`, `PasswordResetRequested`, `OrderPaid`, `OrderStatusChanged`, …)
  are handled in `Application/EventHandlers`.
- Handlers render a branded HTML template (`EmailTemplateRenderer`) and enqueue via `IEmailQueue`.
- `BackgroundEmailQueue` dispatches on a background scope so request latency is unaffected.
- `BrevoEmailService` POSTs to `/smtp/email`. Swap the queue for Hangfire/Service Bus later
  without touching callers.

## 4. Templates included
Welcome · EmailVerification · ForgotPassword · PasswordResetConfirmation · Otp ·
OrderConfirmation · OrderStatusUpdate · PaymentReceipt.

Placeholders use `{{Name}}` substitution (e.g. `{{FirstName}}`, `{{VerificationLink}}`, `{{OrderNumber}}`).

## 5. Test
Register a user and check the log (dev) or your inbox (prod). Verify the verification link
resolves to `App:FrontendBaseUrl/verify-email?email=&token=`.
