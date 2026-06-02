# Sign Dashboard

Full-stack sign shop dashboard built with Next.js, Tailwind CSS, ASP.NET Core Web API, EF Core, and MariaDB.

## Projects

- `SignDashboard.Api` - ASP.NET Core Web API for CRM, ticketing, inventory, quotes, job orders, and file uploads.
- `SignDashboard.Agent` - local storage agent for customer-managed drives, NAS devices, or fileservers.
- `sign-dashboard-web` - Next.js dashboard UI.

## MariaDB

Create a database named `sign`, then keep the connection values in the root `.env` file or `SignDashboard.Api/.env`.

The API reads `MARIADB_CONNECTION_STRING` from `.env` before falling back to `appsettings.json`. It checks both the API folder and the parent solution folder.

The Next.js app reads `NEXT_PUBLIC_API_BASE_URL` from `sign-dashboard-web/.env`.

Set `CUSTOMER_FILES_ROOT` to automatically create file-server folders when a CRM customer is created:

```text
CUSTOMER_FILES_ROOT=\\YOUR-SERVER\SignFiles\Customers
```

You can also change this from the dashboard `Settings` tab. The saved MariaDB setting is used before the `.env` fallback.

For SaaS installs where the cloud API cannot access the customer's local drive or NAS, set storage mode to `LocalAgent` in the dashboard. New customer folders and uploaded artwork/proof files are queued in MariaDB, and the installed local agent syncs them inside the customer network.

Local agent `.env` example:

```text
SIGN_DASHBOARD_API_BASE_URL=http://localhost:5206/api
SIGN_DASHBOARD_AGENT_NAME=shop-fileserver-01
SIGN_DASHBOARD_AGENT_TOKEN=change-this-agent-token
SIGN_DASHBOARD_CUSTOMER_FILES_ROOT=\\YOUR-SERVER\SignFiles\Customers
SIGN_DASHBOARD_AGENT_POLL_SECONDS=15
```

Run the agent:

```powershell
dotnet run --project .\SignDashboard.Agent
```

The token must match `LOCAL_AGENT_TOKEN` in the API settings.

Create the downloadable Windows ZIP for the dashboard button:

```powershell
.\scripts\package-agent.ps1
```

The API serves the package at:

```text
http://localhost:5206/api/agent/download
```

New customer folders use this format:

```text
0001_Customer-Name/
  01_Admin/
  02_Quotes/
  03_Artwork/
  04_Proofs/
  05_Production/
  06_Install/
  07_Invoices/
  08_Photos/
  09_Archive/
```

## Run

API:

```powershell
dotnet restore
dotnet run --project .\SignDashboard.Api
```

The API runs `EnsureCreated` on startup, so an empty `sign` database will get the first schema automatically.

Web:

```powershell
cd .\sign-dashboard-web
npm install
npm run dev
```

Open `http://localhost:3000`.

## Live Dashboard

The API exposes a SignalR hub at:

```text
http://localhost:5206/hubs/dashboard
```

The Next.js dashboard connects to that hub automatically. When tickets, CRM records, inventory, quotes, job orders, or files change, the API broadcasts `dashboardChanged` and connected browsers refresh their data.

## Ticket Board

Tickets are shown as a live production board with these columns:

```text
Pending | Design | Production | Install | Done
```

Each ticket card shows priority, customer, assignee, and due-date urgency. Existing older statuses such as `Open`, `In Review`, `Closed`, and `Converted` are mapped into the new columns automatically in the dashboard.

## Artwork Uploads

Ticket cards include an `Art` action. Designers can upload artwork directly to a ticket with status, uploader name, notes, and automatic versioning.

Accepted artwork types:

```text
.ai, .eps, .pdf, .svg, .psd, .tif, .tiff, .png, .jpg, .jpeg, .cdr
```

Artwork files are saved under the configured customer file root:

```text
0001_Customer-Name/
  Jobs/
    TICKET-0001_Ticket-Name/
      02_Artwork/
      03_Proofs/
      04_Print-Ready/
```

In `LocalAgent` mode, uploads are first staged by the API under `Uploads/AgentStaging`, then the agent downloads the queued file job and writes it to the same relative path on the configured local/NAS root.

## Proofing

Ticket cards include a proof action. Designers can upload proof PDFs/images with status, designer, sent-to, approval metadata, and notes.

Proof statuses:

```text
Draft, Sent, Revision Needed, Approved, Rejected
```

Accepted proof types:

```text
.pdf, .png, .jpg, .jpeg
```

Proofs are versioned and saved under:

```text
0001_Customer-Name/
  Jobs/
    TICKET-0001_Ticket-Name/
      03_Proofs/
```

Customer review links are generated for proof uploads:

```text
http://localhost:3000/proofs/review/{secure-token}
```

Customers can preview/download the proof, add notes, and choose `Approve Proof` or `Request Revision`. The API stores the decision, comment, response date, and IP address, then broadcasts a SignalR dashboard update.
