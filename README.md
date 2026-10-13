# CommercePilot

A considered storefront for everyday technology, built with Next.js, ASP.NET Core, and PostgreSQL.

Browse 32 fictional products across audio, workspace, everyday essentials, and gaming. Search, combine category and price filters, check availability, sort results, and inspect product details. Every catalog response comes from PostgreSQL through the .NET API.

This version is a **catalog preview**. Accounts, carts, checkout, MCP tools, and the AI assistant are not implemented yet. Products and specifications are fictional; there are no real payments.

## Run locally

Install Docker Desktop with Linux containers and start it. From the repository root, run:

```powershell
./scripts/start.ps1
```

The script generates a local database password in the ignored `.env` file, builds the application, applies the database migration, seeds missing catalog entries, and waits for readiness. The first run downloads container images and dependencies.

Open **[localhost:3000](http://localhost:3000)**.

| Service | Address |
| --- | --- |
| Storefront | http://localhost:3000 |
| API readiness | http://localhost:5080/health/ready |
| API liveness | http://localhost:5080/health/live |
| Development OpenAPI document | http://localhost:5080/openapi/v1.json |
| PostgreSQL | localhost:5438 |

All published ports bind to loopback. This Compose configuration is for local development: it enables development OpenAPI and a single-instance migration/seed bootstrap. Do not expose it as a production deployment.

Without PowerShell, copy `.env.example` to `.env`, replace the placeholder with a randomly generated **alphanumeric** password, then run:

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
| `web` | Responsive storefront, product artwork, catalog gateway |
| `src/Commerce.Domain` | Product model |
| `src/Commerce.Application` | Catalog contracts and input validation |
| `src/Commerce.Infrastructure` | EF Core persistence, migrations, catalog queries, demo seed |
| `src/Commerce.Api` | HTTP endpoints, dependency injection, health checks |
| `scripts` | Local startup and running-application verification |

The API owns query validation. Prices use `numeric(12,2)` with an explicit currency. Database constraints reject negative prices and stock. Search escapes SQL `LIKE` metacharacters; paging has a stable tie-breaker. The gateway exposes only catalog reads and does not forward arbitrary upstream paths.

## API

```http
GET /api/products?search=wireless&category=Audio&maxPrice=200&sort=price-asc&page=1&pageSize=12
GET /api/products/{id}
GET /api/categories
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

The named PostgreSQL volume persists across container rebuilds and `docker compose down`. Removing that volume deletes the local catalog database.
