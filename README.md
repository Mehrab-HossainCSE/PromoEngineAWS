# PromoEngine

Multi-tenant retail promotion management. Users sign in with email and password or continue in
guest mode, pick a subscription, and that subscription unlocks promotion creation. Every tenant
gets its own database, provisioned from the email they signed up with.

```
PromoEngine/
├── PromoBackend/     .NET 10 Web API  (control plane + per-tenant promotion data)
└── PromoFrontend/    Angular 20 SPA
```

## Running it

Two terminals:

```powershell
# API on http://localhost:5193
cd PromoBackend
dotnet run --project src/PromoEngine.Api

# SPA on http://localhost:4200
cd PromoFrontend
npm start
```

The catalog database and its three subscription plans are created on first start. Tenant databases
are created on the PostgreSQL server named in `appsettings.json` (`localhost:5432`), so everything
shows up in psql or pgAdmin under that server.

Both connection strings are Npgsql-format and live in `appsettings.json` — `ConnectionStrings:Catalog`
for the control plane, and `Tenancy:Local:ServerConnectionString`, which points at the `postgres`
maintenance database because `CREATE DATABASE` has to be issued from a connection to some *other*
database. Change the username and password there to match your server.

### PostgreSQL specifics

Two things behave differently from SQL Server and are handled in
[PostgresConventions](PromoBackend/src/PromoEngine.Infrastructure/Postgres/PostgresConventions.cs)
rather than scattered through the query code:

- **Case sensitivity.** SQL Server ran on a case-insensitive default collation, so every string
  comparison the application made ignored case without asking. PostgreSQL does not. The columns
  that were relying on it — barcodes, emails, tenant slugs, campaign and template codes — carry a
  non-deterministic ICU collation (`case_insensitive`) that restores exactly the old behaviour for
  equality, `IN`, `DISTINCT`, `GROUP BY` and unique indexes. This needs a PostgreSQL built with ICU,
  which every mainstream 13-and-later build is.

  PostgreSQL refuses `LIKE` against a non-deterministic collation, so the promotion search — the one
  place doing a `Contains` — uses `ILIKE` instead and the description columns are left on the
  default collation.

- **Timestamps.** Npgsql maps `DateTimeOffset` to `timestamptz` and rejects any value whose offset
  is not zero, so a client posting `2026-01-01T00:00:00+06:00` would have failed. A model-wide
  converter normalises every `DateTimeOffset` to UTC on write. `timestamptz` records an instant and
  not the writer's offset; every comparison in the application is an instant comparison, so results
  are unchanged.

Tenant database names are also capped at PostgreSQL's 63-byte identifier limit, with a short hash
replacing what gets cut. PostgreSQL truncates a longer name silently, which could otherwise collapse
two tenants onto one database. SQL Server allowed 128, so this only affects names that were already
near-unworkable.

### Schema creation

Both contexts use `EnsureCreated`, so **no EF migrations are needed** — creating a database also
creates every table for every `DbSet` on it: `Promotions`, `Campaigns`, `Offers`, `OfferConditions`,
`OfferRewards`, `OfferItems`, `OfferLocations`, `OfferProducts`.

The trade-off: `EnsureCreated` never alters a database that already exists, so a tenant created
before a model change would fail on every read of the new column. `TenantSchemaSyncService` closes
that gap — on start up it walks every provisioned tenant and
[TenantSchemaReconciler](PromoBackend/src/PromoEngine.Infrastructure/Provisioning/TenantSchemaReconciler.cs)
adds the missing tables, columns and indexes. It is additive by default — nothing is dropped just
because it is absent from the model, which would delete anything a DBA added alongside ours. The
one exception is the reconciler's `RetiredTables` constant, a hand-written list of tables the
application has genuinely removed; a name goes in it only once the code that read the table is gone
and its data is dead, and the drop is logged with the row count that went with it.
(`PromotionProducts` is there now: the barcode sheet moved from the promotion to the offer, and the
new `OfferProducts` has a different shape — no discount columns, an offer key instead of a
promotion key — so there was nothing to carry across. Sheets uploaded before that change have to be
re-uploaded against an offer.) Turn the whole sync off with `Tenancy:AutoSyncSchemaOnStartup`.
To move to versioned migrations later, swap `EnsureCreatedAsync` for `MigrateAsync` in
[TenantSchemaInitializer](PromoBackend/src/PromoEngine.Infrastructure/Provisioning/TenantSchemaInitializer.cs).

## The flow

| Step | What happens |
|---|---|
| Continue as guest | Short-lived token. Can browse plans and all 13 offer templates, nothing else. |
| Register | Tenant + admin user created. **No database yet** — nothing is provisioned for someone who abandons signup. |
| Choose a plan | Subscription recorded, tenant database queued on a background worker. |
| Guest chooses a plan | Sent to `/get-started` to supply email, password, company and contact details first — the email names the database. |
| Provisioning | `/workspace` polls until the database reports ready, then the subscription flips to Active. |
| Promotions | Unlocked only with an active subscription **and** a ready database. |

## Multi-tenancy

A **catalog** database (`PromoEngineCatalog`) holds tenants, users, plans and provisioning state.
Each tenant additionally gets `PromoEngine_Tenant_<slug>` holding only its own promotions, offers,
conditions, rewards, items, locations and point-of-sale product rows.

Requests carry a `tenant_id` claim. `TenantResolutionMiddleware` loads that tenant's connection
string into a request-scoped `ITenantContext`, and every promotion query opens its context through
`ITenantDbContextFactory` — so a request can only ever reach the database named in its own token.

### Provisioning

`ITenantProvisioner` has two implementations, chosen by `Tenancy:Provisioner`:

- **`Local`** (default) — creates the database on a local PostgreSQL server. No AWS credentials needed.
- **`Aws`** — `AwsRdsTenantProvisioner`, in either mode:
  - `DatabaseOnSharedInstance` (default): one database per tenant on an existing RDS instance. Seconds.
  - `DedicatedInstance`: `CreateDBInstance` per tenant, waits for it to come up. Minutes.

Both apply the identical schema through `TenantSchemaInitializer`, so a tenant behaves the same
either way. To switch to AWS, set in `appsettings.json`:

```json
"Tenancy": {
  "Provisioner": "Aws",
  "Aws": {
    "Region": "us-east-1",
    "SharedInstanceIdentifier": "promo-shared",
    "SharedInstanceEndpoint": "promo-shared.abc123.us-east-1.rds.amazonaws.com",
    "MasterUsername": "promoadmin",
    "MasterPassword": "…",
    "Port": 5432,
    "Engine": "postgres"
  }
}
```

Move `MasterPassword` and the generated tenant connection strings to AWS Secrets Manager before
production; they sit in configuration and the catalog table today so the flow can be exercised
end to end.

## Offer templates

Thirteen templates across two levels, sourced from the Oracle Retail Pricing promotions guide.
Each is described once in `OfferTemplateCatalog` — which conditions it takes, whether it allows a
price restriction, a fixed price, an application limit, a distribution rule, and what its reward
item list means. `GET /api/offer-templates` serves that metadata, so:

- the Angular wizard renders all thirteen from one generic component, and
- `OfferValidator` rejects any offer that does not match its template.

| Level | Templates |
|---|---|
| Item | Get Y for Discount · Buy X Get Discount · Spend X Get Discount · Buy X Get Y for Discount · Spend X Get Y for Discount · Buy X of Single Item · Buy X and Y Get Discount · Buy X and Y Get Z for Discount · Buy X Get GWP · Spend X Get GWP |
| Transaction | Get Discount · Buy X Get Discount · Spend X Get Discount |

Plans gate them: Starter 3 templates, Professional 11 (adds transaction level, emergency offers,
spreadsheet upload), Enterprise all 13 (adds gift with purchase).

## Publishing an offer to a point of sale

Offers describe a discount the way a merchandiser thinks about it — templates, conditions,
rewards, merchandise hierarchy. A till does not have that hierarchy at the counter; it has a
barcode. **Offer wizard → Rewards → Excel Upload**, next to *Add items*, takes the flattened,
barcode-keyed list of the products that offer covers, so an external point of sale such as
EasyPOS can resolve a scanned item to a discount.

The sheet belongs to the offer, not to the promotion above it. That is what makes the whole
thing hang together: **the offer's own reward is the discount for every row on its sheet**, so
the sheet carries no price columns at all and can never disagree with the offer it belongs to.
One offer, one sheet, one discount.

Rows are parsed when you choose the file and stored with the offer when you save it, in
`OfferProducts` on the tenant database, deleted with the offer. Nothing is written before you
press Save, so abandoning the wizard leaves nothing behind. Re-uploading the same file replaces
that file's own rows, so uploading twice never doubles them; the **Replace them** / **Add to
them** choice decides whether rows from *other* files survive. Copying an offer copies its
sheet, which is what tiered offers want.

Headings go in the first row and are matched loosely — case, spaces and underscores are ignored,
so `Bar Code`, `BARCODE`, `bar_code`, `UPC` and `EAN` all resolve to the same column. Long numeric
barcodes are read as integers, so Excel's `8.90123456789E+12` still saves as `8901234567890`.

| Column | Meaning | Also accepts |
|---|---|---|
| **Barcode** *(required)* | What the till sends | Bar Code, UPC, EAN, GTIN |
| **Action** | `Include` takes the discount, `Exclude` withholds it. **Blank means Include** | Include/Exclude, Inclusion |
| **GetApplicablePromotions** | `Yes` / `No` — is this row served to the till at all? | Applicable, Get Applicable |
| PromoTypeId | Matched against the request's `PromotypeID` | Promo Type, Promotion Type |
| SiteCode | Matched against `SiteCode` | Store Code, Outlet |
| CustomerTier | Matched against `CustomerTier` | Tier, Membership Tier |
| Item, Style Code, Description, Department, Class, Subclass, Brand, Vendor, Supplier Site | Carried for display | Item Code / SKU, Style, Product Name, Dept, Brand Name, Site |

An uploaded product **is** a discounted product: a row that says nothing about include or exclude
is included. `Exclude` exists to carve a barcode back out of an otherwise included sheet, and the
grid under *Add items* lets you flip any row either way after the upload without editing the file.

`GetApplicablePromotions` is what makes the sheet a publishing decision rather than a data dump: a
sheet can carry the whole range while exposing only part of it. **A sheet with no such column has
every row served** — the upload says so explicitly rather than implying the flag was supplied.

Blank means "any" for the three scope columns, so a row with no `SiteCode` runs at every store, and
one with no `CustomerTier` applies to every tier. Rows without a barcode, and duplicates of an
earlier barcode, are skipped and reported as warnings rather than failing the file.

This is the only spreadsheet upload in the application: a POS never uploads anything, it only calls
the API below. Upload needs a plan with `AllowsSpreadsheetUpload` — Professional or Enterprise;
Starter gets a 403 explaining why — and is capped by `ItemUpload` in `appsettings.json`
(5 MB, 5,000 rows).

### What EasyPOS calls

`POST /api/External/get-applicable-promotions`, authenticated with the `HEADER-API-KEY` header
(`ExternalPromotionApi:ApiKey`) rather than a JWT — it is a server-to-server integration, not a
signed-in user. It returns the promotions applicable to a basket:

```jsonc
// → { "TenantId": "acme-gmail-com", "SiteCode": "G184", "CustomerTier": "Silver",
//     "PromotypeID": "5",
//     "Items": [{ "Barcode": "6156BA16419876", "UnitPrice": 3000, "InvoiceQty": 2 }] }
[
  {
    "promoId": 1001,
    "promoNo": "[1001] Winter clearance",
    "slabId": 2, "slabNo": "2", "slabDesc": "Buy any 2 (MOV:5K) get 15%",
    "IscrossCategory": 0,
    "promoOffers": [{ "offerId": 2, "offer": "Discount Percentage 15.00%" }]
  }
]
```

### Which database it reads

**`TenantId` is required.** The endpoint is anonymous — there is no signed-in user to infer a
tenant from — so the request has to name the customer whose promotions it is asking about, and
that identifier chooses the tenant database everything below is read from.

Any of the tenant's three unique identifiers works, so an integrator can use whichever one they
were handed rather than a fourth scheme invented for them:

| Send | Example | Where to find it |
|---|---|---|
| Tenant id | `6f1c…-…-…` | **Account → Tenant id** |
| Tenant slug | `acme-gmail-com` | **Account → Tenant slug** |
| Account email | `ops@acme.com` | the address the workspace was registered with |

A missing `TenantId` returns 400 `{"message":"TenantId is required"}`; an unknown one 400
`{"message":"Unknown TenantId '…'"}`; one whose database is still being provisioned 400 saying so.
Nothing in those replies names a tenant the caller did not already name, so a wrong value cannot
be used to enumerate customers.

`ExternalPromotionApi:TenantSlug` pins the deployment to one tenant. Set, it is also a whitelist:
a request naming any other tenant is refused, which is what a single-customer install wants
because the shared API key would otherwise read any customer's promotions. Left blank, the
request's `TenantId` chooses freely. **The API key is shared across tenants — pin the slug, or
issue per-tenant keys, before the key goes to more than one integrator.**

### Where the discount comes from

Always from the offer's reward. A basket line is answered when:

- an uploaded row carries its barcode, is marked `Include`, and has `GetApplicablePromotions` set;
- the row's site, promotion type and tier match the request (blank matches anything);
- the offer is not cancelled or rejected and today is inside its date window;
- the offer's promotion is not cancelled, rejected or completed;
- the offer's locations allow the request's `SiteCode`;
- and the offer's buy/spend conditions are met, measured against *the covered barcodes the basket
  carries* — a till has no merchandise hierarchy, so the offer's own item rules cannot be
  evaluated at the counter, and the sheet defines participation instead.

Workflow status is deliberately not gated beyond cancelled and rejected: the upload is the
publishing decision, and this integration does not wait on the approval flow. **Dates are
gated** — an offer starting next week must not discount anything today.

An offer that never stated a discount is not served at all, and **never comes back as
`Discount Percentage 0.00%`**: a zero discount looks like a working answer and silently applies
nothing, which is worse than reporting no applicable promotion.

**An item with no applicable promotion simply is not in the array**, and a basket that matches
nothing returns `[]` with 200. That is the only shape the integration document defines for "no
discount available"; there is no separate body for it. A bad key returns 401
`{"message":"Authentication failed"}` and a request with no items 400, as documented.

Offers with no uploaded sheet still answer the till from their hierarchy rules, so this upload adds
a source rather than replacing the existing behaviour. Where an offer *has* uploaded rows, those
win and its item rules are not consulted — the sheet is the retailer's explicit statement of what
the till should see.

## Backend layout

```
src/
├── PromoEngine.Domain/          entities, enums, OfferTemplateCatalog
├── PromoEngine.Infrastructure/
│   ├── Catalog/                 CatalogDbContext (shared control plane)
│   ├── Tenancy/                 TenantDbContext, ITenantContext, context factory
│   ├── Provisioning/            ITenantProvisioner, Local + AWS RDS, background worker
│   └── Security/                PBKDF2 hashing, JWT
└── PromoEngine.Api/
    ├── Endpoints/               auth, subscriptions, promotions/offers/locations/campaigns
    ├── Services/                onboarding, promotion service, validator, access gate
    └── Middleware/              tenant resolution, current user
```

### Rules the API enforces

- **Price event lead time** — an offer cannot start within `Pricing:PriceEventProcessingDays`
  (default 2) unless it is flagged as an emergency offer, which itself needs a plan that allows it.
- **Template shape** — required conditions, condition counts (Buy X and Y needs ≥ 2), qualifying
  items, valid discount types, reward item lists, percentages ≤ 100.
- **Status flow** — Worksheet → Submitted → Approved → Active. Approved and active offers cannot
  be edited until reopened; only active offers can be cancelled; cancellation needs a reason.
- **Plan limits** — promotion counts, offers per promotion, template entitlements.

## Frontend layout

```
src/app/
├── core/          models, typed API client, auth signals, interceptor, guards
└── features/
    ├── welcome/ auth/ plans/ onboarding/ workspace/    signup and subscription flow
    ├── promotions/                                     list, detail, offer wizard, item selector
    └── templates/ account/                             reference and tenant admin
```

State is Angular signals, routes are lazy-loaded, and `workspaceGuard` is what keeps promotions
behind a subscription and a provisioned database. The design system lives entirely in
`src/styles.scss` (tokens, light and dark, no UI framework dependency).

Point the SPA at a different API by editing `src/environments/environment.ts`.
