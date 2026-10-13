# CommercePilot

A considered storefront for everyday technology, built with Next.js, ASP.NET Core, and PostgreSQL.

Browse 32 fictional products across audio, workspace, everyday essentials, and gaming. Search, combine category and price filters, check availability, sort results, and inspect product details. Sign in with a local demo account to save a personal shopping bag, change quantities, or remove items. Catalog data, cart contents, and totals come from PostgreSQL through the .NET API.

This version supports **catalog browsing, demo accounts, and persistent shopping bags**. Checkout, MCP tools, and the AI assistant are not implemented yet. Products and specifications are fictional; there are no real payments.

## Run locally

Install Docker Desktop with Linux containers and start it. From the repository root, run:

```powershell
./scripts/start.ps1
```

The script generates a local database password and two unique demo passwords in the ignored `.env` file, builds the application, applies migrations, seeds missing catalog entries/accounts, and waits for readiness. Existing catalog and cart data are preserved. The first run downloads container images and dependencies.

Open **[localhost:3000](http://localhost:3000)**.

To view your locally generated sign-in credentials:

```powershell
./scripts/demo-accounts.ps1
```

The accounts are `alex@commercepilot.local` and `sam@commercepilot.local`, with separate bags. Click **Sign in**, choose an account, and enter its local password. Open a product to add it to your bag. Refreshing the page, signing out and back in, or rebuilding the API preserves the bag. Quantities must be between 1 and 99 and cannot exceed current stock. A bag does not reserve inventory.

| Service | Address |
| --- | --- |
| Storefront | http://localhost:3000 |
| API readiness | http://localhost:5080/health/ready |
| API liveness | http://localhost:5080/health/live |
| Development OpenAPI document | http://localhost:5080/openapi/v1.json |
| PostgreSQL | localhost:5438 |

All published ports bind to loopback. This Compose configuration is for local development: it enables development OpenAPI and a single-instance migration/seed bootstrap. Do not expose it as a production deployment.

Without PowerShell, copy `.env.example` to `.env`, replace the database placeholder with a randomly generated **alphanumeric** password, and supply two distinct demo passwords (at least 12 characters with uppercase, lowercase, digits, and a symbol). Then run:

```sh
docker compose up --build -d
```

Stop the application while preserving catalog data:

```sh
docker compose down
```

Ollama and model downloads are not required for this catalog preview. The storefront uses original vector illustrations. Google Fonts is optional; local system fonts are the fallback when offline.

## Architecture

```text
Browser -> Next.js same-origin catalog gateway -> ASP.NET Core API
                                                -> ICatalogService
                                                -> EF Core / PostgreSQL
```

| Directory | Responsibility |
| --- | --- |
| `web` | Responsive storefront, sign-in and bag dialogs, same-origin gateways |
| `src/Commerce.Domain` | Product and cart item models |
| `src/Commerce.Application` | Catalog/cart contracts and input validation |
| `src/Commerce.Infrastructure` | EF Core persistence, Identity users, migrations, catalog/cart services, demo seeds |
| `src/Commerce.Api` | Authenticated HTTP endpoints, CSRF validation, rate limiting, health checks |
| `tests/Commerce.IntegrationTests` | Real PostgreSQL tests for identity, ownership, quantity/stock validation, concurrent mutations |
| `scripts` | Local startup and running-application verification |

The API owns query validation. Prices use `numeric(12,2)` with an explicit currency. Database constraints reject negative prices and stock. Search escapes SQL `LIKE` metacharacters; paging has a stable tie-breaker. The gateway exposes only catalog reads and does not forward arbitrary upstream paths.

Cart mutations share an application service that obtains prices from the database. A PostgreSQL row lock on the authenticated owner serializes concurrent mutations, including the first addition to an empty bag. The API obtains the owner from the authenticated principal, not client-supplied IDs. Cart reads recalculate prices and availability; unavailable quantities are flagged for correction.

## Sessions and request protection

ASP.NET Core Identity hashes passwords and issues an HttpOnly, SameSite=Strict session cookie with an eight-hour sliding expiration. Cookies require HTTPS outside Development; loopback HTTP is used for the local Compose demo. Every mutation, including sign-in and sign-out, requires an identity-bound antiforgery token. The web gateway additionally checks the browser Origin against `APP_ORIGIN` (default `http://localhost:3000`), limits forwarded routes/methods, and does not automatically retry mutations.

The login endpoint permits 20 attempts per minute per upstream IP, and Identity locks an account for five minutes after five failed password checks. The local gateway shares an upstream IP. A production deployment needs trusted proxy configuration and a suitable distributed rate-limit policy.

Data Protection keys persist in the local PostgreSQL database so rebuilding the API does not invalidate sessions. Keys are not separately encrypted at rest in this local configuration; protect them with a certificate or managed key service for a hosted deployment. Neither credentials nor session cookies are stored in browser localStorage. Account/cart responses disable caching.

## API

```http
GET /api/products?search=wireless&category=Audio&maxPrice=200&sort=price-asc&page=1&pageSize=12
GET /api/products/{id}
GET /api/categories
GET /api/auth/csrf
GET /api/auth/session
POST /api/auth/login
POST /api/auth/logout
GET /api/cart
POST /api/cart/items
PUT /api/cart/items/{productId}
DELETE /api/cart/items/{productId}
```

Supported filters: `search`, `category`, `minPrice`, `maxPrice`, `inStock`. Sort values: `featured`, `price-asc`, `price-desc`, `name`. Page size is limited to 48. Invalid filters return HTTP 400; missing product IDs return HTTP 404. API and database failures produce an explicit storefront error instead of fabricated fallback products.

## Development

For live editing, install .NET 10 SDK and Node.js 24 LTS. Initialize `.env` using the startup script once, then stop the application containers and keep the database running:

```powershell
docker compose stop api web
docker compose up -d postgres
dotnet tool restore
dotnet restore
```

In the API terminal, read the locally generated password without placing it in shell history:

```powershell
$dbPassword = (Get-Content .env | Where-Object { $_ -match '^POSTGRES_PASSWORD=' }) -replace '^POSTGRES_PASSWORD=', ''
$env:ConnectionStrings__Commerce = "Host=localhost;Port=5438;Database=commercepilot;Username=commercepilot;Password=$dbPassword"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Initialize = 'true'
$env:DemoAccounts__AlexPassword = (Get-Content .env | Where-Object { $_ -match '^DEMO_ALEX_PASSWORD=' }) -replace '^DEMO_ALEX_PASSWORD=', ''
$env:DemoAccounts__SamPassword = (Get-Content .env | Where-Object { $_ -match '^DEMO_SAM_PASSWORD=' }) -replace '^DEMO_SAM_PASSWORD=', ''
dotnet run --project src/Commerce.Api --urls http://localhost:5080
```

In another terminal:

```powershell
cd web
npm ci
npm run dev
```

The web gateway defaults to `http://127.0.0.1:5080`; override it with the server-side `API_BASE_URL` environment variable if necessary. Database credentials never enter browser configuration.

## Verification

Against the running Compose stack:

```powershell
./scripts/verify.ps1
```

This checks health, seeded data, filtering, search escaping, sorting, paging, product details, input rejection, and the web gateway against real PostgreSQL data.

For automated identity/cart integration tests, with PostgreSQL running:

```powershell
./scripts/test.ps1
```

The fixture creates a uniquely named test database, applies the real migrations, and removes only that test database afterward. It never resets the development catalog or bags. Tests cover unauthenticated access, secure cookie attributes, sign-out, CSRF rejection, ownership isolation, tampered prices, invalid quantities, stock bounds, and concurrent additions. A PostgreSQL account with permission to create a test database is required (the local Compose account has this permission).

Build checks:

```powershell
dotnet build CommercePilot.slnx --configuration Release
cd web
npm run typecheck
npm run build
```

## Troubleshooting

- **Docker cannot connect:** start Docker Desktop and select Linux containers.
- **A service is unavailable:** inspect `docker compose ps` and `docker compose logs api web postgres`.
- **A port is occupied:** stop the conflicting process or change the host port mapping in `compose.yaml` and the verification URL parameters.
- **Database authentication fails after editing `.env`:** restore the original password. PostgreSQL initializes credentials only when the volume is first created; changing the environment does not rotate an existing database password.
- **Catalog does not load:** verify the readiness endpoint and the gateway's `API_BASE_URL`. The retry button performs a fresh request.
- **Sign-in fails:** run `scripts/demo-accounts.ps1` to view your local credentials. Updating `.env` does not overwrite passwords for existing accounts. After repeated failed attempts, wait five minutes for the account lockout to expire.
- **The storefront rejects a request origin:** use `http://localhost:3000`, or set `APP_ORIGIN` to the exact scheme/host/port you intend to use and restart the web service.

The named PostgreSQL volume persists across container rebuilds and `docker compose down`. Removing that volume deletes the local catalog database.
