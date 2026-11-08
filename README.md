# Otzar HaChochma for AI agents (IT-741)

Lets AI agents work with the licensed Otzar HaChochma desktop app without computer-use (screenshots). Nothing in the Otzar installation (`C:\OtzarApp`, `E:\`) is modified. The tools only start the app with a standard Chromium switch and talk to it locally.

Background research: [research-browser-access.md](research-browser-access.md).

## 1. Start Otzar with the debugging port

Close Otzar first, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\start-otzar-cdp.ps1
```

This opens the Chrome DevTools Protocol (CDP) on `http://127.0.0.1:9222`, on this computer only.

## 2. Connect an agent to the live app (browser automation)

Any CDP-capable tool can attach to the real app window and read or click it by DOM, not by pixels:

- Playwright MCP: `npx @playwright/mcp --cdp-endpoint http://127.0.0.1:9222`
- Chrome DevTools MCP: `npx chrome-devtools-mcp --browser-url=http://127.0.0.1:9222`
- Code: `chromium.connectOverCDP("http://127.0.0.1:9222")` (Playwright)

The main window is the page whose URL contains `/dist/index.html` and does not contain `tabs_toolbar`.

To watch it as a person in Chrome, open `chrome://inspect`, click Configure, add `localhost:9222`, then click inspect on "אוצר החכמה".

## 3. Read-only JSON gateway

```powershell
node .\gateway\otzar-gateway.mjs
```

It listens on `http://127.0.0.1:7979` and needs Node 22 or later.

| Endpoint | Returns |
|---|---|
| `GET /health` | Whether the Otzar main window is reachable |
| `GET /page-text?max=20000` | Visible text of the main window |
| `GET /otzar/api/books/<id>` | Book details (name, authors, place, year...) |
| `GET /otzar/api/books/pageName/<id>/<page>` | Internal page id |
| `GET /otzar/api/categorytypes`, `/otzar/api/categories/...` | Library structure |
| `GET /otzar/<other read path>` | Other read-only internal routes on the allowlist |

Only GET is relayed. Paths that could change state (reset, logout, updates, cache, purchase, license and so on) are refused with 403.

## Security notes

- While Otzar runs with the debugging port, any program on this computer can control it. Start it this way only when agents need it.
- Do not add `--remote-allow-origins=*`. CDP clients don't need it, and it makes it easier for web pages to try to connect.
- The gateway accepts only `127.0.0.1` / `localhost` host headers and sends no CORS headers.
- Keep everything local. Do not expose ports 9222 or 7979 to the network.
