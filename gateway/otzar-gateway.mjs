// IT-741: read-only local HTTP gateway to Otzar HaChochma's in-process API, over CDP.
// Requires Node 22+ (global fetch + WebSocket). No dependencies.
// Otzar must be running with --remote-debugging-port (see ../start-otzar-cdp.ps1).
// Never touches any Otzar file: it only evaluates GET calls inside the running app.

import http from "node:http";

const HOST = "127.0.0.1";
const PORT = Number(process.env.OTZAR_GATEWAY_PORT || 7979);
const CDP = process.env.OTZAR_CDP_URL || "http://127.0.0.1:9222";
const CALL_TIMEOUT_MS = 60_000;
const ALLOWED_HOSTS = new Set([`127.0.0.1:${PORT}`, `localhost:${PORT}`]);

// Read-only routes of the internal API that may be relayed.
const ALLOW_PREFIXES = [
  "api/books", "api/pages", "api/categories", "api/categorytypes", "api/foldertypes",
  "api/authors", "api/words", "api/freesearch", "api/images", "api/encyclopedia",
  "api/general/o-ver", "api/general/fonts", "api/general/gimatria", "api/general/cities",
  // Bare "data/", "table/", "txt/" removed in round 3: they answer [] for any input; the real
  // routes are under api/books/ (api/books/data, api/books/txt, ...), covered by "api/books".
  "pagedata/", "pagename/", "pageid/", "subjects", "yesod",
  "byPos/", "shas/", "splits/", "relations", "favs", "otzar_ver", "get_ver",
];
// Anything that looks like it changes state, even on a GET, is refused.
const BLOCK_RE =
  /(reset|logout|login|shutdown|apply|clear|reload|create|crt|delete|cancel|psw|password|purchase|launch|store|encrypt|snapshot|save|set-|disconnect|reconnect|connect|backup|lic|code|track|update-info|import|mail|register)/i;

function isAllowed(path) {
  if (BLOCK_RE.test(path)) return false;
  return ALLOW_PREFIXES.some((p) => {
    const base = p.replace(/\/$/, "");
    return path === base || path.startsWith(base + "/");
  });
}

const MAX_DECODE_ROUNDS = 8;
// Round 3: allow-list of characters for a decoded segment. ASCII letters, digits, '_', '-', '.'.
// Everything else is refused, including ':' (drive letters "C:", NTFS streams "book:x"),
// '\', '%', '?', '#', control chars, spaces and non-ASCII.
const GOOD_SEGMENT_RE = /^[A-Za-z0-9_.-]+$/;

// Round 2 (IT-741 review round 1): canonicalize the raw /otzar/ path so that the string
// we check is exactly the string the app's router will see.
//  1. Percent-decode repeatedly until stable (double/triple encoding collapses here).
//  2. Split into segments. Reject empty, ".", ".." segments and any segment with a
//     character outside GOOD_SEGMENT_RE
//     (no resolution needed: anything that could climb is refused outright).
//  3. Allowlist / blocklist the decoded, canonical path.
//  4. Relay "/" + each segment encodeURIComponent'ed. Express decodes each param once,
//     which yields exactly the checked segment; this is asserted before relaying.
// Returns { path, relay } or { error }.
function canonicalize(rawRest) {
  let s = rawRest;
  for (let i = 0; ; i++) {
    let d;
    try { d = decodeURIComponent(s); } catch { return { error: "malformed percent-encoding" }; }
    if (d === s) break;
    if (i >= MAX_DECODE_ROUNDS) return { error: "too many encoding layers" };
    s = d;
  }
  if (s.includes("%")) return { error: "percent sign in path" };
  const segments = s.split("/");
  if (segments.some((seg) => seg === "." || seg === ".." || !GOOD_SEGMENT_RE.test(seg))) {
    return { error: "bad path segment" };
  }
  const path = segments.join("/");
  if (path.includes("..")) return { error: "dot-dot in path" };
  if (!isAllowed(path)) return { error: "path not allowed" };
  const relay = "/" + segments.map(encodeURIComponent).join("/");
  // What the app will decode must be the very string that was checked.
  const seen = relay.slice(1).split("/").map(decodeURIComponent).join("/");
  if (seen !== path || !isAllowed(relay.slice(1))) return { error: "relay mismatch" };
  return { path, relay };
}

async function findMainPage() {
  const res = await fetch(`${CDP}/json/list`, { signal: AbortSignal.timeout(5000) });
  const pages = (await res.json()).filter((p) => p.type === "page" && p.url.includes("/dist/index.html"));
  const main = pages.find((p) => !p.url.includes("tabs_toolbar") && !p.url.includes("whatsnew")) || null;
  if (!main) throw new Error("Otzar main window not found over CDP");
  return main;
}

// Runs a JS expression in the main Otzar page and returns its (JSON) value.
async function evaluate(expression) {
  const page = await findMainPage();
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  try {
    await new Promise((ok, fail) => {
      ws.onopen = ok;
      ws.onerror = () => fail(new Error("CDP websocket error"));
    });
    return await new Promise((ok, fail) => {
      const timer = setTimeout(() => fail(new Error("CDP call timed out")), CALL_TIMEOUT_MS);
      ws.onmessage = (m) => {
        const d = JSON.parse(m.data);
        if (d.id !== 1) return;
        clearTimeout(timer);
        if (d.error) return fail(new Error(d.error.message));
        if (d.result.exceptionDetails) return fail(new Error(d.result.exceptionDetails.exception?.description || "evaluation failed"));
        ok(d.result.result.value);
      };
      ws.send(JSON.stringify({ id: 1, method: "Runtime.evaluate", params: { expression, awaitPromise: true, returnByValue: true } }));
    });
  } finally {
    ws.close();
  }
}

// Only GET is ever issued. url is embedded with JSON.stringify, so it cannot inject code.
function relayExpression(url) {
  return `(async () => {
    const a = globalThis.AXIOS_OFFLINE;
    if (!a) return { error: "AXIOS_OFFLINE not available" };
    try {
      const r = await a.get(${JSON.stringify(url)});
      let d = r.data;
      const bytes = d instanceof ArrayBuffer ? new Uint8Array(d) : ArrayBuffer.isView(d) ? new Uint8Array(d.buffer, d.byteOffset, d.byteLength) : null;
      if (bytes) {
        let s = ""; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
        return { status: r.status || 200, base64: btoa(s) };
      }
      return { status: r.status || 200, json: d === undefined ? null : d };
    } catch (e) {
      return { status: e?.response?.status || 502, error: String(e?.message || e) };
    }
  })()`;
}

function send(res, status, body, type = "application/json; charset=utf-8") {
  res.writeHead(status, { "Content-Type": type, "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" });
  res.end(typeof body === "string" || Buffer.isBuffer(body) ? body : JSON.stringify(body));
}

const server = http.createServer(async (req, res) => {
  try {
    if (!ALLOWED_HOSTS.has(req.headers.host || "")) return send(res, 403, { error: "bad host" });
    if (req.method !== "GET") return send(res, 405, { error: "only GET is allowed" });
    const u = new URL(req.url, `http://${req.headers.host}`);

    if (u.pathname === "/health") {
      try {
        const page = await findMainPage();
        return send(res, 200, { ok: true, cdp: CDP, page: page.url });
      } catch (e) {
        return send(res, 503, { ok: false, cdp: CDP, error: e.message });
      }
    }

    if (u.pathname === "/page-text") {
      const max = Math.min(Number(u.searchParams.get("max")) || 20000, 200000);
      const text = await evaluate(`document.body.innerText.slice(0, ${max})`);
      return send(res, 200, text, "text/plain; charset=utf-8");
    }

    // Work on the raw request target, not on the URL-normalized pathname, so nothing is
    // resolved or decoded behind our back.
    const rawTarget = req.url || "";
    if (rawTarget.startsWith("/otzar/") || u.pathname.startsWith("/otzar/") || u.pathname === "/otzar") {
      if (!rawTarget.startsWith("/otzar/")) return send(res, 403, { error: "path not allowed" });
      // No query string is ever forwarded; no allowlisted route needs one.
      if (rawTarget.includes("?")) return send(res, 403, { error: "query string not allowed" });
      const c = canonicalize(rawTarget.slice("/otzar/".length));
      if (c.error) return send(res, 403, { error: c.error });
      const out = await evaluate(relayExpression(c.relay));
      if (out?.base64 !== undefined) return send(res, out.status, Buffer.from(out.base64, "base64"), "application/octet-stream");
      if (out?.error) return send(res, out.status || 502, { error: out.error });
      return send(res, out.status, out.json);
    }

    return send(res, 404, { error: "not found", endpoints: ["/health", "/page-text?max=N", "/otzar/<api path>"] });
  } catch (e) {
    return send(res, 502, { error: e.message });
  }
});

server.listen(PORT, HOST, () => console.log(`Otzar gateway on http://${HOST}:${PORT} (CDP ${CDP})`));
