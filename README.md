# Kabuor Dine

QR ordering and payments for restaurants, cafés, hotels and bars, by Kabuor Technologies.

Guests scan the QR on their table, order from their own phone with their choices, and pay by M-Pesa. The kitchen
and bar each see their own tickets as they arrive, the floor sees which tables need a waiter, and every bill is a
proper invoice in the books: stock goes down, the sale is in the ledger and KRA eTIMS signs it.

## What's in it

| Part | Where |
|---|---|
| Guest QR menu (`/m/:table`) | `web/src/dine/GuestMenu.tsx` |
| Floor, kitchen and bar screens, menu, tables and QR cards, the day's report, settings (`/dine/*`) | `web/src/dine/` |
| Landing page (`/`), sign-up (`/signup`), sign-in (`/signin`) | `web/src/dine/Landing.tsx`, `web/src/pages/Auth.tsx` |
| Dine API: guest side `/api/dine/m/{table}`, staff side `/api/dine` | `src/Kabuor.Erp.Api/Dine/` |
| The engine: sign-in and roles, invoices, stock, M-Pesa, eTIMS, the ledger, plans and billing | `src/Kabuor.Erp.Api/` |

Kabuor Dine runs on the Kabuor ERP engine, so a restaurant can switch on the full ERP (payroll, purchasing,
several branches) from its plan screen without moving anything. The ERP's own screens are the "Back office".

## Run it locally

```
# API (SQLite file in development) on http://localhost:5080
cd src/Kabuor.Erp.Api && dotnet run
# Web on http://localhost:5173 (proxies /api to the API)
cd web && npm install && npm run dev
```

Open http://localhost:5173, choose **Start**, and sign up a restaurant. Load the sample Kenyan menu from **Menu**,
print the QR cards from **Tables & QR**, and open a table's link on your phone to order as a guest.

Tests: `dotnet test` (SQLite; set `ERP_TEST_PG` to a PostgreSQL connection string to run them on PostgreSQL too).

## Deploy

One Docker image serves the API and the built web app (`Dockerfile`; `render.yaml` is a Render blueprint).

| Setting | What |
|---|---|
| `DATABASE_URL` | PostgreSQL connection string. Migrations run on start. |
| `JWT__KEY` | 32+ random characters. Required; never change it once set (it also seals stored M-Pesa and eTIMS keys). |
| `APP__PUBLICURL` | The public https address: M-Pesa callbacks and the table QR codes use it. |
| `PLATFORM__ADMINS` | Emails of the platform admins (they sign up like anyone; the console asks for two-step sign-in). |
| `BILLING__MPESA__*` | Kabuor's own Daraja app, for restaurants paying their plan (test payments without it, in development only). |
| `SENTRY__DSN`, `AFRICASTALKING__*` | Optional: error reports, SMS. |

Each restaurant adds its own Daraja keys in Back office → Settings → M-Pesa to send payment prompts to guests;
without them guests ask for the bill and the waiter records cash, card or an M-Pesa code.

There are no shared or default logins: every account opens with its own password.

© Kabuor Technologies. All rights reserved.
