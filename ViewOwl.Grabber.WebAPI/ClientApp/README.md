# ViewOwl Dashboard — React Frontend

## Development

```bash
# Install dependencies
npm install

# Start dev server (with hot reload)
npm run dev
# Open http://localhost:5173

# Build for production
npm run build
# Output goes to ../wwwroot/dashboard/
```

## Stack

- React 18
- Vite 6
- SignalR Client (`@microsoft/signalr`)
- React Flow (coming in next task)

## API Proxy

Dev server proxies all requests to the ASP.NET backend:

| Path | Target |
|------|--------|
| `/api/*` | `http://localhost:5085` |
| `/hubs/*` | `http://localhost:5085` (WebSocket) |

## Deployment

Run `npm run build` before `dotnet publish`. The build output lands in
`../wwwroot/dashboard/` and is served as static files by ASP.NET Core at `/dashboard/`.
