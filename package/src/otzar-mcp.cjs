// IT-742: Otzar HaChochma MCP server (stdio), portable.
// One CommonJS file, Node built-ins only, so it can be packed into a single executable (Node SEA).
//
// What it does:
//   * Finds the Otzar install (OTZARAPPCD env var, then shortcuts, then known folders; x64 and x86).
//   * Finds the CDP port of a running Otzar from its own command line, or starts Otzar with a free
//     port. It never kills or restarts Otzar, and never writes any file of Otzar. After the user
//     closes Otzar, the next call starts it again, with launch-loop protection (IT-779).
//   * "otzar-mcp.exe shortcut" writes one .lnk on the user's desktop (IT-779); nothing else is written.
//   * Exposes MCP tools over stdin/stdout: status, search, search_hits, get_page_text,
//     get_page_image, book_info.
//
// Security (kept from IT-741, tightened):
//   * No listening socket at all. The agent talks to this process over stdio only.
//   * CDP is reached on 127.0.0.1 only, and only if /json/version identifies the browser as Otzar.
//   * No generic relay. Every internal API call is built here from validated integers (plus the
//     app's own search-file token), checked by the IT-741 segment allow-list, and must match one
//     route of a fixed list. Everything is GET, except one fixed POST route (page OCR text).
//   * Values enter Runtime.evaluate only through JSON.stringify or Number(); search text is typed
//     with Input.insertText (data, not code).
"use strict";

const { spawn, spawnSync } = require("node:child_process");
const fs = require("node:fs");
const net = require("node:net");
const path = require("node:path");

const SERVER_NAME = "otzar-hachochma";
const SERVER_VERSION = "1.0.0";
const CDP_HOST = "127.0.0.1";
// Versions this package was tested with. A difference is a warning, not an error.
const TESTED_WITH = { electron: "18.2.0", otzarVer: 19, updatesVer: 7.40001 };
const LAUNCH_WAIT_MS = Math.min(Math.max(Number(process.env.OTZAR_MCP_LAUNCH_WAIT_SEC) || 120, 5), 600) * 1000;
const CALL_TIMEOUT_MS = 60_000;
const OCR_CHUNK = 200; // the OCR route returns at most ~200 words per call
const COVERAGE_QUERY = "שבת"; // "shabbat": appears in almost every book
const COVERAGE_MIN_RATIO = 0.5;
// Launch-loop protection (IT-742 incident, IT-779): at most START_CAP automatic starts per
// START_WINDOW_MS, and none after a start that never answered on its debugging port.
const START_CAP = 2;
const START_WINDOW_MS = 10 * 60_000;
// IT-779: desktop shortcut that opens Otzar through this tool, so the tool can always connect.
const SHORTCUT_NAME = "אוצר החכמה ל-AI";
const SHORTCUT_ARGS = "launch --shortcut";
const SHORTCUT_HINT = `from the desktop shortcut "${SHORTCUT_NAME}" (made once with: otzar-mcp.exe shortcut)`;

const log = (...a) => process.stderr.write(`[otzar-mcp] ${a.join(" ")}\n`);

// ---------------------------------------------------------------------------------------------
// Route guard (IT-741 segment allow-list + a fixed list of routes)
// ---------------------------------------------------------------------------------------------
const GOOD_SEGMENT_RE = /^[A-Za-z0-9_.-]+$/;
const ROUTES_GET = [
  /^api\/books\/\d{1,12}$/, // book metadata
  /^api\/books\/\d{1,12}\/pages$/, // page list (labels, word offsets)
  /^api\/books\/pageData\/\d{1,12}\/\d{1,7}$/, // page meta by position
  /^api\/books\/pageName\/\d{1,12}\/\d{1,7}$/, // image page id by position
  /^api\/freesearch\/words\/search\/[A-Za-z0-9]{1,128}\/\d{1,12}\/\d{1,12}\/\d{1,12}$/, // hit offsets
];
const ROUTE_POST_OCR = /^api\/books\/\d{1,12}\/pages\/\d{1,12}\/ocr$/;

function checkedPath(p, routes) {
  const segs = p.split("/");
  if (segs.some((s) => s === "." || s === ".." || !GOOD_SEGMENT_RE.test(s))) throw new Error("internal: bad path segment");
  if (!routes.some((r) => r.test(p))) throw new Error("internal: route not allowed");
  return p;
}
function int(v, name, min = 0, max = 999999999999) {
  const n = Number(v);
  if (!Number.isInteger(n) || n < min || n > max) throw new UserError(`${name} must be an integer between ${min} and ${max}`);
  return n;
}
class UserError extends Error {}

// ---------------------------------------------------------------------------------------------
// Windows discovery: install folder, running process, CDP port
// ---------------------------------------------------------------------------------------------
// System tools by full path: the MCP host may start this server with a minimal PATH.
const SYS32 = path.join(process.env.SystemRoot || process.env.SYSTEMROOT || process.env.windir || "C:\\Windows", "System32");

// Throws if PowerShell could not run, so a failed lookup never looks like an empty answer.
// `env` passes values (paths, texts) as environment variables, so they are never part of the script.
function ps(script, env) {
  const r = spawnSync(path.join(SYS32, "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], {
    encoding: "utf8", windowsHide: true, timeout: 30_000, env: env ? { ...process.env, ...env } : process.env,
  });
  if (r.error || r.status !== 0) throw new Error(`PowerShell failed: ${r.error ? r.error.message : String(r.stderr || "").trim().slice(0, 200)}`);
  return (r.stdout || "").trim();
}

function regValue(key, name) {
  const r = spawnSync(path.join(SYS32, "reg.exe"), ["query", key, "/v", name], { encoding: "utf8", windowsHide: true, timeout: 10_000 });
  const m = /REG_(?:EXPAND_)?SZ\s+(.+)$/m.exec(r.stdout || "");
  return m ? m[1].trim() : null;
}

function exeIn(dir) {
  if (!dir) return null;
  const exe = path.join(dir.replace(/^"|"$/g, ""), "otzar.exe");
  return fs.existsSync(exe) ? exe : null;
}

// Returns { exe, source } or null. Read-only.
function findInstall() {
  const envDirs = [
    ["OTZARAPPCD (process)", process.env.OTZARAPPCD],
    ["OTZARAPPCD (machine)", regValue("HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment", "OTZARAPPCD")],
    ["OTZARAPPCD (user)", regValue("HKCU\\Environment", "OTZARAPPCD")],
  ];
  for (const [source, dir] of envDirs) {
    const exe = exeIn(dir);
    if (exe) return { exe, source };
  }
  // Shortcuts on the desktops and in the Start menus whose target is otzar.exe.
  let lnk = "";
  try { lnk = ps(`
    $dirs = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory'),
              [Environment]::GetFolderPath('StartMenu'), [Environment]::GetFolderPath('CommonStartMenu'))
    $sh = New-Object -ComObject WScript.Shell
    foreach ($d in $dirs) { if ($d -and (Test-Path $d)) {
      Get-ChildItem -LiteralPath $d -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        $t = $sh.CreateShortcut($_.FullName).TargetPath
        if ($t -and ($t -match '(?i)\\\\otzar\\.exe$') -and (Test-Path -LiteralPath $t)) { $t; break }
      } } }`).split(/\r?\n/)[0];
  } catch (e) { log("shortcut lookup failed:", e.message); }
  if (lnk) return { exe: lnk, source: "shortcut" };
  // Last resort: the standard layout on any fixed drive, x64 first on 64-bit Windows.
  const arch = process.env.PROCESSOR_ARCHITEW6432 || process.env.PROCESSOR_ARCHITECTURE || "";
  const archs = /64/.test(arch) ? ["x64", "x86"] : ["x86", "x64"];
  // Only local fixed disks (DriveType 3), so a dead network or removable drive cannot stall discovery.
  let drives = [];
  try {
    drives = ps(`Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object { $_.DeviceID }`)
      .split(/\r?\n/).map((d) => d.trim()).filter((d) => /^[A-Za-z]:$/.test(d));
  } catch (e) { log("drive list failed:", e.message); }
  for (const d of drives) {
    for (const a of archs) {
      const exe = exeIn(`${d}\\OtzarApp\\OtzarLocal\\launcher\\bin\\${a}\\app`);
      if (exe) return { exe, source: "standard folder" };
    }
  }
  return null;
}

// Running otzar.exe processes: [{ pid, exe, cmd }].
// Throws a UserError when the process list cannot be read: an unknown state must never be
// treated as "Otzar is not running", or a second Otzar would be started (IT-742 incident).
function otzarProcesses() {
  let out;
  try {
    out = ps(`@(Get-CimInstance Win32_Process -Filter "Name='otzar.exe'" | Select-Object ProcessId,ExecutablePath,CommandLine) | ConvertTo-Json -Compress`);
  } catch (e) {
    // Fallback without command lines: enough to know whether Otzar is running at all.
    const r = spawnSync(path.join(SYS32, "tasklist.exe"), ["/FI", "IMAGENAME eq otzar.exe", "/FO", "CSV", "/NH"], { encoding: "utf8", windowsHide: true, timeout: 15_000 });
    if (r.error || r.status !== 0) {
      throw new UserError(`Cannot tell whether Otzar HaChochma is running (the process list is unavailable), so it is not started. ${e.message}`);
    }
    return String(r.stdout || "").split(/\r?\n/).filter((l) => /^"otzar\.exe"/i.test(l)).map((l) => ({ pid: Number(l.split('","')[1]), exe: null, cmd: "" }));
  }
  if (!out) return [];
  let j;
  try { j = JSON.parse(out); } catch { throw new UserError("Cannot read the process list, so Otzar HaChochma is not started."); }
  return (Array.isArray(j) ? j : [j]).map((p) => ({ pid: p.ProcessId, exe: p.ExecutablePath, cmd: p.CommandLine || "" }));
}

function freePort() {
  return new Promise((ok, fail) => {
    const s = net.createServer();
    s.unref();
    s.on("error", fail);
    s.listen(0, CDP_HOST, () => { const { port } = s.address(); s.close(() => ok(port)); });
  });
}

async function cdpVersion(port) {
  try {
    const r = await fetch(`http://${CDP_HOST}:${port}/json/version`, { signal: AbortSignal.timeout(3000) });
    const v = await r.json();
    // Only ever drive Otzar, never some other Chromium that happens to expose a debugging port.
    if (!/\botzar\//i.test(v["User-Agent"] || "")) return null;
    return v;
  } catch { return null; }
}

// --- desktop shortcut and popup helpers (Windows Script Host through PowerShell) ---
// PowerShell 5 writes its console code page by default; UTF-8 keeps Hebrew paths intact.
const PS_UTF8 = "[Console]::OutputEncoding = [Text.Encoding]::UTF8;";
function desktopDir() {
  return ps(`${PS_UTF8} [Environment]::GetFolderPath('Desktop')`).split(/\r?\n/)[0].trim();
}
// Returns { target, args } for an existing .lnk, or null when there is no file at that path.
function readShortcut(lnk) {
  if (!fs.existsSync(lnk)) return null;
  const out = ps(`${PS_UTF8} $s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:OTZAR_MCP_LNK); @{ target = $s.TargetPath; args = $s.Arguments } | ConvertTo-Json -Compress`, { OTZAR_MCP_LNK: lnk });
  try { const j = JSON.parse(out); return { target: j.target || "", args: j.args || "" }; } catch { return { target: "", args: "" }; }
}
function writeShortcut(spec) {
  ps(`$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:OTZAR_MCP_LNK)
    $s.TargetPath = $env:OTZAR_MCP_TARGET; $s.Arguments = $env:OTZAR_MCP_ARGS
    $s.WorkingDirectory = $env:OTZAR_MCP_WD; $s.IconLocation = $env:OTZAR_MCP_ICON
    $s.Description = $env:OTZAR_MCP_DESC; $s.WindowStyle = 7
    $s.Save()`, {
    OTZAR_MCP_LNK: spec.path, OTZAR_MCP_TARGET: spec.target, OTZAR_MCP_ARGS: spec.args,
    OTZAR_MCP_WD: spec.workingDir, OTZAR_MCP_ICON: spec.icon, OTZAR_MCP_DESC: spec.description,
  });
}
// A message box for the user (the shortcut has no visible console). Right-to-left, topmost.
function popup(text) {
  // 48 warning icon + 4096 system modal + 0x80000 right-aligned + 0x100000 RTL reading order.
  ps(`[void](New-Object -ComObject WScript.Shell).Popup($env:OTZAR_MCP_TEXT, 0, $env:OTZAR_MCP_TITLE, 48 + 4096 + 0x80000 + 0x100000)`,
    { OTZAR_MCP_TEXT: text, OTZAR_MCP_TITLE: SHORTCUT_NAME });
}
// Brings Otzar's window to the front, best effort (only focus; nothing is changed).
function activate(pids) {
  ps(`$sh = New-Object -ComObject WScript.Shell; foreach ($p in ($env:OTZAR_MCP_PIDS -split ',')) { if ($p -and $sh.AppActivate([int]$p)) { break } }`,
    { OTZAR_MCP_PIDS: pids.filter((p) => Number.isInteger(p)).join(",") });
}
const isSea = (() => { try { return require("node:sea").isSea(); } catch { return false; } })();

// Seams for the offline tests (package/test). Production code always uses the real functions.
const deps = {
  otzarProcesses: () => otzarProcesses(), findInstall: () => findInstall(), spawn: (...a) => spawn(...a),
  now: () => Date.now(),
  selfExe: () => (isSea ? process.execPath : null),
  desktopDir: () => desktopDir(), readShortcut: (p) => readShortcut(p), writeShortcut: (s) => writeShortcut(s),
  popup: (t) => popup(t), activate: (pids) => activate(pids),
};

// starts: times of the automatic starts by this process (for the cap).
// noAnswer: the last automatic start has not answered on its debugging port yet. It is cleared by
// any successful connection (also to an Otzar the user opened from the shortcut).
const state = { port: null, install: null, launchedByUs: false, starts: [], noAnswer: false, lastStartPort: null,
  warnings: new Set(), checked: false, coverage: null };

const MSG_ALREADY_OPEN =
  "Otzar HaChochma is open without the agent connection (it was opened in the usual way, so no debugging port). " +
  "Tell the user: save your work and close Otzar, then call this tool again (it reopens Otzar with the connection), " +
  `or open Otzar ${SHORTCUT_HINT}. Nothing was closed or changed. Do not retry until the user has done this.`;

function connected(port) {
  state.port = port;
  state.noAnswer = false;
  return port;
}

// Makes sure Otzar runs with a CDP port and returns the port. Starts Otzar if it isn't running
// (unless OTZAR_MCP_NO_LAUNCH=1). Never kills or restarts it.
// waitForWindow=false returns as soon as the debugging port answers (used by the shortcut).
async function ensureOtzar({ waitForWindow = true } = {}) {
  if (state.port && (await cdpVersion(state.port))) return connected(state.port);
  state.port = null;
  const envPort = process.env.OTZAR_CDP_PORT;
  if (envPort) {
    const p = int(envPort, "OTZAR_CDP_PORT", 1, 65535);
    if (await cdpVersion(p)) return connected(p);
  }
  const procs = deps.otzarProcesses();
  if (procs.length) {
    for (const p of procs) {
      const m = /--remote-debugging-port=(\d{1,5})\b/.exec(p.cmd);
      if (m && (await cdpVersion(Number(m[1])))) return connected(Number(m[1]));
    }
    const ours = state.lastStartPort && procs.some((p) => p.cmd.includes(`--remote-debugging-port=${state.lastStartPort}`));
    if (ours) {
      throw new UserError(
        "Otzar HaChochma, started by this tool, is running but its agent connection does not answer (it is still loading, or it shows an error). " +
        "Wait half a minute and call the tool again. If it still fails, tell the user: check Otzar on the screen and close it if it shows an error.");
    }
    throw new UserError(MSG_ALREADY_OPEN);
  }
  // Otzar is not running: the user closed it, or it never ran. It may be started again
  // (IT-779), but not in a loop (IT-742 incident: three instances in two minutes).
  if (process.env.OTZAR_MCP_NO_LAUNCH === "1") throw new UserError("Otzar HaChochma is not running, and automatic start is disabled (OTZAR_MCP_NO_LAUNCH=1). Tell the user to open Otzar " + SHORTCUT_HINT + ".");
  if (state.noAnswer) {
    throw new UserError(
      "Otzar HaChochma was started by this tool earlier, but it never answered on its agent connection, so the tool does not start it again by itself (loop protection). " +
      `Tell the user: if Otzar shows an error, close it; then open Otzar ${SHORTCUT_HINT}, and call this tool again. Do not retry until the user has done this.`);
  }
  const now = deps.now();
  state.starts = state.starts.filter((t) => now - t < START_WINDOW_MS);
  if (state.starts.length >= START_CAP) {
    const mins = Math.max(1, Math.ceil((state.starts[0] + START_WINDOW_MS - now) / 60_000));
    throw new UserError(
      `Otzar HaChochma was started by this tool ${state.starts.length} times in the last ${START_WINDOW_MS / 60_000} minutes and was closed again each time, so it is not started again now (loop protection). ` +
      `Tell the user: open Otzar ${SHORTCUT_HINT}, then call this tool again; or try again in ${mins} minute(s).`);
  }
  const inst = (state.install = state.install || deps.findInstall());
  if (!inst) throw new UserError("Otzar HaChochma installation not found (no OTZARAPPCD variable, no shortcut to otzar.exe, no standard folder).");
  // Otzar started from a stripped environment fails in its main process (IT-742 incident:
  // "Failed to load image ... app.ico"). Only start it with a normal user environment.
  const missing = ["SystemRoot", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP"].filter((k) => !process.env[k]);
  if (missing.length) {
    throw new UserError(`Otzar HaChochma is not running, and this tool was started without a normal user environment (missing: ${missing.join(", ")}), so it will not start Otzar. Tell the user to open Otzar ${SHORTCUT_HINT}, or to run "otzar-mcp.exe launch" from a normal command window.`);
  }
  const port = await freePort();
  log(`starting ${inst.exe} with CDP port ${port}`);
  // Only one extra switch. --remote-allow-origins is deliberately not passed.
  const child = deps.spawn(inst.exe, [`--remote-debugging-port=${port}`], { cwd: path.dirname(inst.exe), detached: true, stdio: "ignore", windowsHide: false });
  child.unref();
  // Recorded before waiting: a start that never answers blocks the next automatic start.
  state.launchedByUs = true;
  state.starts.push(now);
  state.noAnswer = true;
  state.lastStartPort = port;
  const t0 = Date.now();
  while (Date.now() - t0 < LAUNCH_WAIT_MS) {
    await sleep(1000);
    if (await cdpVersion(port)) {
      connected(port);
      if (waitForWindow) await waitReady(Math.max(5000, LAUNCH_WAIT_MS - (Date.now() - t0)));
      return port;
    }
  }
  throw new UserError(
    `Otzar HaChochma was started, but its agent connection (port ${port}) did not answer within ${LAUNCH_WAIT_MS / 1000}s. ` +
    "If Otzar is still loading, call the tool again in a minute. Otherwise tell the user: check Otzar on the screen and close it if it shows an error. " +
    "The tool will not start Otzar again by itself until it has connected once.");
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------------------------------------
// CDP
// ---------------------------------------------------------------------------------------------
async function mainPage(port) {
  const r = await fetch(`http://${CDP_HOST}:${port}/json/list`, { signal: AbortSignal.timeout(5000) });
  const pages = (await r.json()).filter((p) => p.type === "page" && /\/dist\/index\.html/.test(p.url));
  const main = pages.find((p) => !/tabs_toolbar|whatsnew/.test(p.url));
  if (!main) return null;
  // Same-host check on the websocket URL as well.
  const u = new URL(main.webSocketDebuggerUrl);
  if (u.hostname !== CDP_HOST || Number(u.port) !== port) throw new Error("unexpected CDP websocket host");
  return main;
}

async function session() {
  const port = await ensureOtzar();
  const page = await mainPage(port);
  if (!page) throw new UserError("Otzar's main window is not available yet (still loading?). Try again in a few seconds.");
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  // Bounded, so a half-closed Otzar can never hang the call (and every call queued after it).
  await new Promise((ok, fail) => {
    const t = setTimeout(() => { try { ws.close(); } catch {} fail(new Error("CDP websocket did not open")); }, 10_000);
    ws.onopen = () => { clearTimeout(t); ok(); };
    ws.onerror = () => { clearTimeout(t); fail(new Error("CDP websocket error")); };
  });
  let id = 0;
  const pending = new Map();
  ws.onmessage = (m) => {
    const d = JSON.parse(m.data);
    const p = pending.get(d.id);
    if (p) { pending.delete(d.id); clearTimeout(p.t); d.error ? p.fail(new Error(d.error.message)) : p.ok(d.result); }
  };
  ws.onclose = () => { for (const p of pending.values()) p.fail(new Error("CDP connection closed")); pending.clear(); };
  const send = (method, params = {}, timeout = CALL_TIMEOUT_MS) => new Promise((ok, fail) => {
    const i = ++id;
    const t = setTimeout(() => { pending.delete(i); fail(new Error(`CDP ${method} timed out`)); }, timeout);
    pending.set(i, { ok, fail, t });
    ws.send(JSON.stringify({ id: i, method, params }));
  });
  const evaluate = async (expression, timeout) => {
    const r = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }, timeout);
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description?.split("\n")[0] || "evaluation failed");
    return r.result.value;
  };
  return { port, send, evaluate, close: () => { try { ws.close(); } catch {} } };
}

async function withSession(fn) {
  const s = await session();
  try { return await fn(s); } finally { s.close(); }
}

// Serializes tool calls: they share one UI (the search panel) and one app.
let chain = Promise.resolve();
const serial = (fn) => { const r = chain.then(fn, fn); chain = r.catch(() => {}); return r; };

// --- page-side helpers (strings evaluated inside Otzar) ---
// Locates the Vue 2 root store and the free-search component without relying on UI text:
// the content-search input has an id that starts with "fs_input_".
const PAGE_HELPERS = `
  const __root = () => { const a = document.querySelector('#app'); return a && a.__vue__; };
  const __fsInputs = () => [...document.querySelectorAll('input[id^="fs_input_"]')];
  const __fsComp = () => { for (const i of __fsInputs()) { let e = i; while (e && !e.__vue__) e = e.parentElement; if (e && e.__vue__ && e.__vue__.freeSearchBookListStore) return e.__vue__; } return null; };
  const __fsStore = () => { const c = __fsComp(); return c && c.freeSearchBookListStore; };
`;

function apiGet(s, p) {
  const route = checkedPath(p, ROUTES_GET);
  return s.evaluate(`(async () => { const r = await globalThis.AXIOS_OFFLINE.get(${JSON.stringify(route)}); return r.data; })()`);
}

// ---------------------------------------------------------------------------------------------
// Version / capability check and coverage check
// ---------------------------------------------------------------------------------------------
async function waitReady(ms) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    try {
      const ok = await withSession((s) => s.evaluate(`(() => { ${PAGE_HELPERS} const r = __root(); return !!(globalThis.AXIOS_OFFLINE && r && r.$store && r.$store.state.offline && r.$store.state.offline.licence && __fsComp()); })()`, 5000));
      if (ok) return true;
    } catch {}
    await sleep(1500);
  }
  return false;
}

async function probe(s) {
  const v = await cdpVersion(s.port);
  const electron = (/Electron\/([\d.]+)/.exec(v?.["User-Agent"] || "") || [])[1] || null;
  const app = await s.evaluate(`(() => { ${PAGE_HELPERS}
    const r = __root(); const st = r && r.$store; const lic = st && st.state.offline && st.state.offline.licence || {};
    let webpack = false; try { webpack = Array.isArray(globalThis.webpackJsonp); } catch {}
    return {
      otzarVer: lic.otzarVer ?? null, updatesVer: lic.updatesVer ?? null,
      network: lic.Network ?? null,
      libraryBooks: st ? st.getters['bookList/getBooksCount'] ?? null : null,
      caps: { api: !!globalThis.AXIOS_OFFLINE, store: !!st, searchInput: __fsInputs().length > 0,
              searchStore: !!__fsStore(), pageCanvas: !!document.querySelector('canvas.img-canvas'), webpack },
    }; })()`);
  const warnings = [];
  if (electron && electron !== TESTED_WITH.electron) warnings.push(`Otzar runs Electron ${electron}; this tool was tested with ${TESTED_WITH.electron}. If a tool fails, update this tool.`);
  if (app.otzarVer != null && app.otzarVer !== TESTED_WITH.otzarVer) warnings.push(`Otzar disk version ${app.otzarVer}; tested with ${TESTED_WITH.otzarVer}.`);
  if (app.updatesVer != null && app.updatesVer !== TESTED_WITH.updatesVer) warnings.push(`Otzar software update ${app.updatesVer}; tested with ${TESTED_WITH.updatesVer}.`);
  for (const [k, ok] of Object.entries(app.caps)) if (!ok && k !== "pageCanvas") warnings.push(`Capability missing: ${k}. Some tools may not work with this Otzar version.`);
  return { electron, ...app, tested_with: TESTED_WITH, warnings };
}

async function versionCheckOnce() {
  if (state.checked) return;
  state.checked = true;
  try {
    const p = await withSession(probe);
    for (const w of p.warnings) state.warnings.add(w);
  } catch (e) { log("version check failed:", e.message); }
}

async function coverageCheck() {
  const r = await doSearch(COVERAGE_QUERY, 0, 180);
  const ratio = r.library_books ? r.books_with_results / r.library_books : null;
  const ok = r.books_with_results >= 1000 && (ratio == null || ratio >= COVERAGE_MIN_RATIO);
  state.coverage = {
    control_query: COVERAGE_QUERY, books_with_results: r.books_with_results, total_results: r.total_results,
    library_books: r.library_books, ratio: ratio == null ? null : Math.round(ratio * 1000) / 1000, ok,
  };
  if (!ok) {
    state.coverage.warning =
      "Search covers too few books. Otzar's search settings are probably limiting the search " +
      "(a book list or filter is selected, or a setting is stuck). Fix the search settings in Otzar, " +
      "or install the latest Otzar software update. Until then, search results are incomplete.";
  }
  return state.coverage;
}

// ---------------------------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------------------------
async function doSearch(query, limit, timeoutSec) {
  if (typeof query !== "string" || !query.trim()) throw new UserError("query is required");
  if (query.length > 300 || /[\u0000-\u001f\u007f]/.test(query)) throw new UserError("query must be one line, up to 300 characters");
  const q = query.trim();
  const lim = Number(limit);
  return withSession(async (s) => {
    const io = {
      // One evaluation reads the state and, when Otzar is idle on this query, the results too, so
      // the check and the read cannot be split by another search (IT-742 review r1, live race).
      read: () => s.evaluate(`(() => { ${PAGE_HELPERS} const f = __fsStore(); const r = __root();
        if (!f || !r) return null;
        const st = { hid: f.currentHistoryId, searching: !!r.$store.getters['freeSearch/isSearching'], text: f.getFinalTextSearch };
        if (st.searching || st.text !== ${JSON.stringify(q)}) return st;
        const list = (f.getCurrentList && f.getCurrentList.booksList) || [];
        const books = list.slice(0, ${lim}).map((b) => {
          const authors = []; for (let i = 0; i < (b.authors_length || 0); i++) if (b['authors_' + i + '_name']) authors.push(b['authors_' + i + '_name']);
          return { book_id: b.id, name: b.name, volume: b.volume || undefined, authors, place: b.places || undefined,
                   year: b.fromyear || undefined, results: b.fs ? b.fs.results : undefined };
        });
        st.results = { query: f.getFinalTextSearch, total_results: f.totalResults, books_with_results: f.currentListLength,
                       library_books: r.$store.getters['bookList/getBooksCount'] ?? null, sort: f.sortBy, books };
        return st; })()`),
      submit: async () => {
        // UI-driven: focus the visible content-search input, replace its text, press Enter.
        const focused = await s.evaluate(`(() => { ${PAGE_HELPERS}
          const i = __fsInputs().find((x) => x.offsetParent) ; if (!i) return false;
          i.focus(); i.select(); return document.activeElement === i; })()`);
        if (focused) {
          await s.send("Input.insertText", { text: q });
          for (const type of ["keyDown", "keyUp"]) await s.send("Input.dispatchKeyEvent", { type, key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
        } else {
          // The input is hidden (panel collapsed): call the search component's own search method.
          await s.evaluate(`(() => { ${PAGE_HELPERS} const c = __fsComp(); c.doFreeSearch(${JSON.stringify(q)}); return true; })()`);
        }
      },
      wait: sleep,
      now: Date.now,
    };
    const st = await runSearch(io, q, timeoutSec * 1000);
    return st.results;
  });
}

// Waits for the search of q that this call submits, and only for that one (IT-742 review r1):
//  1. If Otzar is already searching, wait until it is idle BEFORE submitting, so a running search
//     can never be mistaken for ours.
//  2. Submit. Our cycle has started once, after the submit, Otzar is seen searching or the
//     history id changes.
//  3. Accept only a state where our cycle has started, Otzar is idle and the store's final text
//     equals q, taken from one read (state and results together). It must repeat unchanged on the
//     next poll, so the list has settled. If another search takes over right after an accepted
//     state, that last state of ours is returned; it was complete and idle.
//  4. Once started, a final text that is neither q nor the text from before the submit means
//     another search replaced ours: error at once. Never return another search's totals.
// io = { read() -> {hid, searching, text, results?} | null, submit(), wait(ms), now() }
async function runSearch(io, q, timeoutMs, { pollMs = 200, startMs = 8000 } = {}) {
  const t0 = io.now();
  const deadline = t0 + timeoutMs;
  const read = async () => {
    const st = await io.read();
    if (!st) throw new UserError("Otzar's search panel was not found. Make sure the main window finished loading.");
    return st;
  };
  const sig = (x) => JSON.stringify([x.hid, x.results ? x.results.total_results : null, x.results ? x.results.books_with_results : null]);
  let st = await read();
  while (st.searching) {
    if (io.now() > deadline) throw new UserError(`Otzar was busy with another search for ${Math.round(timeoutMs / 1000)}s; this search was not started.`);
    await io.wait(pollMs);
    st = await read();
  }
  const before = st;
  await io.submit();
  const t1 = io.now();
  let started = false;
  let candidate = null;
  for (;;) {
    await io.wait(pollMs);
    st = await read();
    if (st.searching || st.hid !== before.hid) started = true;
    const ours = started && !st.searching && st.text === q;
    if (ours) {
      if (candidate && sig(candidate) === sig(st)) return st;
      candidate = st;
    } else if (started && st.text !== q && (st.text !== before.text || (!st.searching && st.hid !== before.hid))) {
      // Another text took over, or Otzar finished a NEW search (new history id) that is not ours.
      if (candidate) return candidate;
      throw new UserError("Another search replaced this one in Otzar. Run the search again.");
    } else {
      candidate = null;
    }
    if (!started && io.now() - t1 > startMs) throw new UserError("The search did not start in Otzar. Check that Otzar is not showing a dialog, then try again.");
    if (io.now() > deadline) throw new UserError(`The search did not finish within ${Math.round(timeoutMs / 1000)}s.`);
  }
}

async function pagesOf(s, book) {
  const pages = await apiGet(s, `api/books/${book}/pages`);
  if (!Array.isArray(pages) || !pages.length) throw new UserError(`Book ${book} has no pages, or it is not in this library (license packages differ between users).`);
  return pages.filter((p) => !p.empty);
}

function resolvePage(pages, args) {
  if (args.page_label != null && args.page_label !== "") {
    const lbl = String(args.page_label).trim();
    const p = pages.find((x) => x.pagedata?.[0]?.letter === lbl);
    if (!p) throw new UserError(`No page labelled "${lbl}" in this book.`);
    return p;
  }
  const pos = int(args.page ?? 1, "page", 1, 1e7);
  const p = pages.find((x) => x.position === pos);
  if (!p) throw new UserError(`Page ${pos} is out of range (the book has ${pages.length} pages).`);
  return p;
}

async function pageWords(s, book, page) {
  const out = [];
  for (const pd of page.pagedata || []) {
    const pageId = int(pd.pageId ?? page.id, "pageId");
    const route = checkedPath(`api/books/${book}/pages/${pageId}/ocr`, [ROUTE_POST_OCR]);
    const total = int(pd.numWords || 0, "numWords");
    const first = int(pd.firstWord || 0, "firstWord");
    // startWord is an absolute word index in the book; the route clips the request to this page's
    // [firstWord, firstWord + numWords) window and returns at most ~200 words. Some indices have no
    // word (graphics, punctuation), so positions:true is asked for as well: it returns the absolute
    // index of every returned word, which is the same numbering as the search hit offsets.
    for (let start = first; start < first + total; start += OCR_CHUNK) {
      const n = Math.min(OCR_CHUNK, first + total - start);
      const base = { x: 0, y: 0, width: int(pd.width || 0, "width"), height: int(pd.height || 0, "height"),
        startWord: start, numWord: n, search: [],
        pageData: { id: int(pd.id, "id"), width: int(pd.width || 0, "width"), height: int(pd.height || 0, "height"),
          margin: Number(pd.margin) || 0, firstWord: first, numWords: total,
          letter: String(pd.letter ?? ""), pageId, userId: Number(pd.userId) || 0 } };
      const [words, idx] = await s.evaluate(`(async () => {
        const a = globalThis.AXIOS_OFFLINE, u = ${JSON.stringify(route)}, b = ${JSON.stringify(base)};
        const t = await a.post(u, Object.assign({}, b, { positions: false }));
        const q = await a.post(u, Object.assign({}, b, { positions: true }));
        return [t.data, q.data]; })()`);
      if (!Array.isArray(words)) continue;
      const pos = Array.isArray(idx) ? idx.map(Number) : [];
      const list = words.map(String);
      if (list.length === pos.length + 1 && list[list.length - 1] === "") list.pop(); // empty terminator
      list.forEach((w, i) => { if (w !== "") out.push({ w, abs: list.length === pos.length ? pos[i] : null }); });
    }
  }
  return out;
}

async function toolGetPageText(args) {
  const book = int(args.book_id, "book_id", 1);
  return withSession(async (s) => {
    const pages = await pagesOf(s, book);
    const page = resolvePage(pages, args);
    const words = await pageWords(s, book, page);
    const maxWords = args.max_words ? int(args.max_words, "max_words", 1, 100000) : words.length;
    const text = words.slice(0, maxWords).map((x) => x.w).filter(Boolean).join(" ");
    return { book_id: book, page: page.position, page_label: page.pagedata?.[0]?.letter || null, pages_in_book: pages.length,
      words: words.length, truncated: maxWords < words.length, text: text || "(no OCR text on this page)" };
  });
}

async function toolSearchHits(args) {
  const book = int(args.book_id, "book_id", 1);
  const maxPages = int(args.max_pages ?? 20, "max_pages", 1, 500);
  const snippets = args.snippets !== false;
  return withSession(async (s) => {
    const fsRef = await s.evaluate(`(() => { ${PAGE_HELPERS} const f = __fsStore(); if (!f) return null;
      const b = (f.getCurrentList?.booksList || []).find((x) => x.id === ${book});
      return b && b.fs ? { fs: String(b.fs.fs), start: b.fs.start, end: b.fs.end, results: b.fs.results, query: f.getFinalTextSearch } : { query: f.getFinalTextSearch }; })()`);
    if (!fsRef || !fsRef.fs) throw new UserError(`Book ${book} is not in the results of the current search${fsRef?.query ? ` ("${fsRef.query}")` : ""}. Run search first.`);
    if (!/^[A-Za-z0-9]{1,128}$/.test(fsRef.fs)) throw new Error("internal: unexpected search-file token");
    const hits = await apiGet(s, `api/freesearch/words/search/${fsRef.fs}/${int(fsRef.start, "start")}/${int(fsRef.end, "end")}/${book}`);
    // Hit offsets are 1-based word numbers; OCR positions and page firstWord are 0-based.
    const offsets = [...new Set((Array.isArray(hits) ? hits : []).map(Number).filter((x) => Number.isInteger(x) && x > 0))]
      .map((x) => x - 1).sort((a, b) => a - b);
    const pages = await pagesOf(s, book);
    const byPage = new Map();
    for (const off of offsets) {
      const p = pages.find((x) => (x.pagedata || []).some((d) => off >= d.firstWord && off < d.firstWord + d.numWords));
      if (!p) continue;
      if (!byPage.has(p)) byPage.set(p, []);
      byPage.get(p).push(off);
    }
    const list = [...byPage.entries()].slice(0, maxPages);
    const outPages = [];
    for (const [p, offs] of list) {
      // Words of one phrase hit are consecutive offsets: group offsets less than 4 words apart.
      const groups = [];
      for (const off of offs) {
        const g = groups[groups.length - 1];
        if (g && off - g[g.length - 1] < 4) g.push(off); else groups.push([off]);
      }
      const item = { page: p.position, page_label: p.pagedata?.[0]?.letter || null, matches: groups.length };
      if (snippets) {
        const words = await pageWords(s, book, p);
        const near = (off) => { const i = words.findIndex((w) => w.abs != null && w.abs >= off); return i < 0 ? null : i; };
        item.snippets = groups.slice(0, 5).map((g) => {
          const marks = new Set(g.map(near).filter((i) => i != null && words[i].abs <= g[g.length - 1]));
          if (!marks.size) return null;
          const lo = Math.min(...marks), hi = Math.max(...marks);
          return words.slice(Math.max(0, lo - 8), hi + 9)
            .map((x, j) => (marks.has(Math.max(0, lo - 8) + j) ? `«${x.w}»` : x.w)).join(" ");
        }).filter(Boolean);
      }
      outPages.push(item);
    }
    return { query: fsRef.query, book_id: book, results_in_book: fsRef.results, pages_with_hits: byPage.size,
      returned_pages: outPages.length, pages: outPages };
  });
}

async function toolGetPageImage(args) {
  const book = int(args.book_id, "book_id", 1);
  const width = int(args.width ?? 1000, "width", 300, 3000);
  return withSession(async (s) => {
    const pages = await pagesOf(s, book);
    const page = resolvePage(pages, args);
    const pos = int(page.position, "position", 1);
    const imgPageId = await apiGet(s, `api/books/pageName/${book}/${pos}`);
    let dataUrl = null;
    let method = "app image loader";
    if (typeof imgPageId === "string" && /^[A-Za-z0-9_.-]{1,64}$/.test(imgPageId)) {
      // Uses the app's own page-image loader (decodes DjVu/PDF exactly like the reader does).
      // The webpack module is looked up by its exported function name, not by a minified id.
      dataUrl = await s.evaluate(`(async () => {
        // The loader is found once through webpack's require and kept under a Symbol key.
        const K = Symbol.for('otzar-mcp.webpackRequire');
        let req = globalThis[K] || null;
        if (!req) {
          const id = 'otzar_mcp_' + Date.now();
          try { globalThis.webpackJsonp.push([[id], { [id]: (m, e, r) => { req = r; } }, [[id]]]); } catch { return null; }
          if (req) Object.defineProperty(globalThis, K, { value: req, enumerable: false });
        }
        if (!req || !req.c) return null;
        const k = Object.keys(req.c).find((k) => req.c[k] && req.c[k].exports && typeof req.c[k].exports.getImageAsDataUrl === 'function');
        if (!k) return null;
        return await req.c[k].exports.getImageAsDataUrl(${book}, ${JSON.stringify(imgPageId)}, { resize: ${width} });
      })()`, 90_000).catch(() => null);
    }
    if (!dataUrl) {
      // Fallback: the page currently shown in the reader (canvas export works in the background).
      method = "visible canvas";
      dataUrl = await s.evaluate(`(() => {
        const m = /#\\/b\\/(\\d+)\\/p\\/(\\d+)/.exec(location.hash);
        if (!m || Number(m[1]) !== ${book} || Number(m[2]) !== ${pos}) return null;
        const c = [...document.querySelectorAll('canvas.img-canvas')].find((x) => x.offsetParent && x.width > 0);
        return c ? c.toDataURL('image/png') : null; })()`);
      if (!dataUrl) throw new UserError("Could not load the page image with this Otzar version. Open the page in Otzar and try again.");
    }
    const m = /^data:(image\/[a-z]+);base64,([A-Za-z0-9+/=]+)$/.exec(dataUrl);
    if (!m) throw new Error("unexpected image data");
    if (m[2].length < 12000 && method === "app image loader") {
      // A tiny image is the app's "error displaying the book" placeholder.
      state.warnings.add("A page image came back as a small placeholder; the book may be outside your license package or unreadable.");
    }
    const png = Buffer.from(m[2].slice(0, 40), "base64");
    const dims = m[1] === "image/png" && png.length >= 24 ? { width: png.readUInt32BE(16), height: png.readUInt32BE(20) } : {};
    return { image: { mimeType: m[1], data: m[2] }, meta: { book_id: book, page: pos, page_label: page.pagedata?.[0]?.letter || null,
      pages_in_book: pages.length, requested_width: width, ...dims, method } };
  });
}

async function toolBookInfo(args) {
  const book = int(args.book_id, "book_id", 1);
  return withSession(async (s) => {
    const raw = await apiGet(s, `api/books/${book}`);
    const b = Array.isArray(raw) ? raw[0] : raw;
    if (!b || !b.id) throw new UserError(`Book ${book} was not found in this library (license packages differ between users).`);
    let pages = [];
    try { pages = await pagesOf(s, book); } catch {}
    const names = (a) => (Array.isArray(a) ? a.map((x) => x.name).filter(Boolean) : []);
    return {
      book_id: b.id, name: b.name, volume: b.volume || null, other_names: names(b.addnames),
      authors: (b.authors || []).map((a) => a.name), places: names(b.places),
      year_from: names(b.fromyear)[0] || null, year_to: names(b.toyear)[0] || null,
      volumes: (b.volumes || []).map((v) => (typeof v === "object" ? { book_id: v.id, volume: v.volume || v.name } : v)),
      main_volume: Array.isArray(b.mainvol) && b.mainvol[0] ? b.mainvol[0].id || b.mainvol[0] : null,
      has_text_layer: b.moredata ? !!b.moredata.isText : null,
      pages: pages.length,
      first_page_label: pages[0]?.pagedata?.[0]?.letter || null,
      last_page_label: pages[pages.length - 1]?.pagedata?.[0]?.letter || null,
    };
  });
}

async function toolStatus(args) {
  const port = await ensureOtzar();
  await waitReady(30_000);
  const p = await withSession(probe);
  for (const w of p.warnings) state.warnings.add(w);
  state.checked = true;
  const out = {
    connected: true, cdp: `${CDP_HOST}:${port}`, started_by_this_tool: state.launchedByUs,
    install: state.install ? state.install.exe : (otzarProcesses()[0]?.exe || null),
    electron: p.electron, otzar_disk_version: p.otzarVer, otzar_update_version: p.updatesVer,
    library_books: p.libraryBooks, capabilities: p.caps, tested_with: p.tested_with,
  };
  if (args.coverage_check) out.coverage = await coverageCheck();
  else if (state.coverage) out.coverage = state.coverage;
  return out;
}

// Argument schemas. validateArgs() enforces them BEFORE any Otzar call (IT-742 review r1).
const BOOK_ID = { type: "integer", minimum: 1, maximum: 999999999999, description: "Book id from search or Otzar." };
const PAGE = { type: "integer", minimum: 1, maximum: 9999999, description: "1-based position of the page in the book." };
const PAGE_LABEL = { type: "string", minLength: 1, maxLength: 20, singleLine: true, description: "Printed page/daf label, e.g. יג." };

const TOOLS = [
  {
    name: "status",
    description: "Connect to Otzar HaChochma (starting it if needed) and report versions, capabilities and warnings. With coverage_check=true it also runs a control search (שבת) and warns if the search covers too few books. Note: the control search replaces what is shown in Otzar's search panel.",
    inputSchema: { type: "object", properties: { coverage_check: { type: "boolean", description: "Run the control search (about 20 seconds)." } }, additionalProperties: false },
    run: toolStatus,
  },
  {
    name: "search",
    description: "Full-text search in all books of the user's Otzar HaChochma library, using Otzar's own search syntax (see the skill). Returns the total number of results, the number of books with results, and the top books (book_id, name, volume, authors, year, results). The search is typed into Otzar's search panel, so the user sees it there too.",
    inputSchema: { type: "object", properties: {
      query: { type: "string", minLength: 1, maxLength: 300, singleLine: true, notBlank: true, description: "Search text in Otzar syntax, e.g. שבת or \"מעשה & בראשית\"." },
      limit: { type: "integer", minimum: 0, maximum: 200, description: "How many books to list (default 20)." },
      timeout_sec: { type: "integer", minimum: 10, maximum: 600, description: "Default 120." },
    }, required: ["query"], additionalProperties: false },
    run: async (a) => {
      const r = await doSearch(a.query, a.limit == null ? 20 : int(a.limit, "limit", 0, 200), a.timeout_sec == null ? 120 : int(a.timeout_sec, "timeout_sec", 10, 600));
      if (state.coverage && !state.coverage.ok) r.coverage_warning = state.coverage.warning;
      return r;
    },
  },
  {
    name: "search_hits",
    description: "For one book from the current search results: the pages where the search matched, with short snippets (the hit word in «»). Run search first.",
    inputSchema: { type: "object", properties: {
      book_id: BOOK_ID,
      max_pages: { type: "integer", minimum: 1, maximum: 500, description: "Default 20." },
      snippets: { type: "boolean", description: "Default true. False is faster (page list only)." },
    }, required: ["book_id"], additionalProperties: false },
    run: toolSearchHits,
  },
  {
    name: "get_page_text",
    description: "OCR text of one page of a book (all words of the page, read in pages of 200 words internally). Give page (1-based position in the book, as in search_hits) or page_label (the printed page/daf label, e.g. יג).",
    inputSchema: { type: "object", properties: {
      book_id: BOOK_ID, page: PAGE, page_label: PAGE_LABEL,
      max_words: { type: "integer", minimum: 1, maximum: 100000 },
    }, required: ["book_id"], additionalProperties: false },
    run: toolGetPageText,
  },
  {
    name: "get_page_image",
    description: "Image (PNG) of one page of a book, rendered by Otzar itself, in the background. Use it to check the OCR text, read tables, vocalization or handwriting.",
    inputSchema: { type: "object", properties: {
      book_id: BOOK_ID, page: PAGE, page_label: PAGE_LABEL,
      width: { type: "integer", minimum: 300, maximum: 3000, description: "Pixel width (default 1000)." },
    }, required: ["book_id"], additionalProperties: false },
    run: toolGetPageImage,
  },
  {
    name: "book_info",
    description: "Bibliographic details of a book: name, volume, authors, place, years, volumes, page count and first/last page labels.",
    inputSchema: { type: "object", properties: { book_id: BOOK_ID }, required: ["book_id"], additionalProperties: false },
    run: toolBookInfo,
  },
];

// ---------------------------------------------------------------------------------------------
// MCP over stdio (newline-delimited JSON-RPC 2.0)
// ---------------------------------------------------------------------------------------------
// Checks args against a tool's inputSchema (object, no unknown keys, required keys, integer and
// string bounds, booleans). Integers may come as numbers or digit strings. Returns clean args.
function validateArgs(schema, args) {
  if (args == null) args = {};
  if (typeof args !== "object" || Array.isArray(args)) throw new UserError("arguments must be an object");
  const props = schema.properties || {};
  const out = {};
  for (const k of Object.keys(args)) if (!(k in props)) throw new UserError(`unknown argument: ${k}`);
  for (const k of schema.required || []) if (args[k] == null) throw new UserError(`${k} is required`);
  for (const [k, p] of Object.entries(props)) {
    const v = args[k];
    if (v == null) continue;
    if (p.type === "integer") {
      const n = typeof v === "string" && /^[0-9]{1,15}$/.test(v) ? Number(v) : v;
      const min = p.minimum ?? 0, max = p.maximum ?? 1e12;
      if (typeof n !== "number" || !Number.isInteger(n) || n < min || n > max) throw new UserError(`${k} must be an integer between ${min} and ${max}`);
      out[k] = n;
    } else if (p.type === "string") {
      if (typeof v !== "string") throw new UserError(`${k} must be a string`);
      if (p.notBlank && !v.trim()) throw new UserError(`${k} is required`);
      if (v.length < (p.minLength ?? 0) || v.length > (p.maxLength ?? 1000)) throw new UserError(`${k} must be ${p.minLength ?? 0} to ${p.maxLength ?? 1000} characters`);
      if (p.singleLine && /[\u0000-\u001f\u007f]/.test(v)) throw new UserError(`${k} must be one line, without control characters`);
      out[k] = v;
    } else if (p.type === "boolean") {
      if (typeof v !== "boolean") throw new UserError(`${k} must be true or false`);
      out[k] = v;
    }
  }
  return out;
}

function reply(msg) { process.stdout.write(JSON.stringify(msg) + "\n"); }

async function callTool(name, args) {
  const tool = TOOLS.find((t) => t.name === name);
  if (!tool) return { content: [{ type: "text", text: `Unknown tool: ${name}` }], isError: true };
  try {
    // Validation first: invalid arguments never reach ensureOtzar (and so can never start Otzar).
    const clean = validateArgs(tool.inputSchema, args);
    const result = await serial(async () => {
      if (name !== "status") { await ensureOtzar(); await versionCheckOnce(); }
      return tool.run(clean);
    });
    const content = [];
    let data = result;
    if (result && result.image) {
      content.push({ type: "image", data: result.image.data, mimeType: result.image.mimeType });
      data = result.meta;
    }
    if (state.warnings.size) data = { ...data, warnings: [...state.warnings] };
    content.unshift({ type: "text", text: JSON.stringify(data, null, 1) });
    return { content };
  } catch (e) {
    return { content: [{ type: "text", text: await errorText(e) }], isError: true };
  }
}

// The text an agent gets for a failed call. A low-level failure (CDP socket closed, fetch failed)
// while Otzar has gone away becomes a plain message, so the agent knows what happened (IT-779).
async function errorText(e) {
  if (e instanceof UserError) return e.message;
  log(e.stack || e.message);
  if (state.port && !(await cdpVersion(state.port))) {
    state.port = null;
    return "Otzar HaChochma was closed, or stopped answering, during this call. Call the tool again once: it reopens Otzar, or says what the user needs to do.";
  }
  return `Error: ${e.message}`;
}

async function handle(msg) {
  const { id, method, params } = msg;
  const isReq = id !== undefined && id !== null;
  try {
    switch (method) {
      case "initialize":
        return isReq && reply({ jsonrpc: "2.0", id, result: {
          protocolVersion: params?.protocolVersion || "2025-06-18",
          capabilities: { tools: {} },
          serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
          instructions: "Read-only access to the user's licensed Otzar HaChochma library on this PC. Call status first in a session. Cite book name and page label for every quote. " +
            "When a tool error says \"Tell the user\", relay that text to the user and wait for them; do not retry in a loop.",
        } });
      case "ping": return isReq && reply({ jsonrpc: "2.0", id, result: {} });
      case "tools/list":
        return isReq && reply({ jsonrpc: "2.0", id, result: { tools: TOOLS.map(({ run, ...t }) => t) } });
      case "tools/call":
        return isReq && reply({ jsonrpc: "2.0", id, result: await callTool(params?.name, params?.arguments) });
      default:
        if (isReq) reply({ jsonrpc: "2.0", id, error: { code: -32601, message: `Method not found: ${method}` } });
    }
  } catch (e) {
    if (isReq) reply({ jsonrpc: "2.0", id, error: { code: -32603, message: e.message } });
  }
}

function serve() {
  let buf = "";
  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (d) => {
    buf += d;
    let i;
    while ((i = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, i).trim();
      buf = buf.slice(i + 1);
      if (!line) continue;
      let msg;
      try { msg = JSON.parse(line); } catch { reply({ jsonrpc: "2.0", id: null, error: { code: -32700, message: "Parse error" } }); continue; }
      handle(msg);
    }
  });
  process.stdin.on("end", () => process.exit(0));
  log(`${SERVER_NAME} ${SERVER_VERSION} ready (stdio)`);
}

// ---------------------------------------------------------------------------------------------
// Desktop shortcut (IT-779)
// ---------------------------------------------------------------------------------------------
// Creates (or updates) the desktop shortcut SHORTCUT_NAME. It runs "otzar-mcp.exe launch --shortcut",
// which opens Otzar with a free debugging port chosen at that moment, or just brings Otzar to the
// front when it already runs with the connection. It never touches Otzar's own shortcut or files:
// it writes one .lnk on the user's desktop, and refuses to replace a file there that is not ours.
async function makeShortcut() {
  const self = deps.selfExe();
  if (!self) throw new UserError('Run "shortcut" from otzar-mcp.exe (the shortcut points to that file).');
  const inst = deps.findInstall();
  if (!inst) throw new UserError("Otzar HaChochma installation not found (no OTZARAPPCD variable, no shortcut to otzar.exe, no standard folder), so no shortcut was made.");
  const desktop = deps.desktopDir();
  if (!desktop || !fs.existsSync(desktop)) throw new UserError("The desktop folder was not found, so no shortcut was made.");
  const lnk = path.join(desktop, `${SHORTCUT_NAME}.lnk`);
  const existing = deps.readShortcut(lnk);
  if (existing && !/(^|\\)otzar-mcp[^\\]*\.exe$/i.test(existing.target)) {
    throw new UserError(`"${lnk}" already exists and is not a shortcut of this tool, so it was left unchanged. Rename or remove it yourself, then run this command again.`);
  }
  const spec = {
    path: lnk, target: self, args: SHORTCUT_ARGS, workingDir: path.dirname(self), icon: `${inst.exe},0`,
    description: "Otzar HaChochma with the connection for AI agents (otzar-mcp)",
  };
  deps.writeShortcut(spec);
  return { shortcut: lnk, target: self, arguments: SHORTCUT_ARGS, otzar: inst.exe, replaced_previous: !!existing };
}

// Text of the shortcut's message box: Hebrew only, written for the person who double-clicked.
// The English messages are for agents ("Tell the user ...") and come out badly in an RTL box,
// so they go to stderr only (IT-779, live test).
function shortcutPopupText(e) {
  const m = e instanceof UserError ? e.message : "";
  if (m === MSG_ALREADY_OPEN) return "אוצר החכמה כבר פתוח, אבל בלי החיבור ל-AI.\nשמרו את העבודה וסגרו את אוצר החכמה, ואז פתחו אותו שוב מקיצור הדרך הזה.";
  if (/did not answer within/.test(m)) return "אוצר החכמה נפתח, אבל החיבור ל-AI עדיין לא עונה.\nאם אוצר החכמה עדיין נטען, חכו לו. אם מופיעה בו הודעת שגיאה, סגרו אותו ופתחו שוב מקיצור הדרך הזה.";
  if (/installation not found/.test(m)) return "לא נמצאה התקנה של אוצר החכמה במחשב הזה.";
  return "לא הצלחנו לפתוח את אוצר החכמה עם החיבור ל-AI.\nנסו שוב בעוד דקה. אם זה חוזר, פתחו את אוצר החכמה מקיצור הדרך הרגיל.";
}

// What the shortcut runs. Returns the exit code. Errors go to a message box, since the window
// is minimized.
async function launchFromShortcut() {
  try {
    await ensureOtzar({ waitForWindow: false });
    if (!state.launchedByUs) {
      // Already running with the connection: the user wants to see it.
      try { deps.activate(deps.otzarProcesses().map((p) => p.pid)); } catch {}
    }
    return 0;
  } catch (e) {
    log(e.message);
    try { deps.popup(shortcutPopupText(e)); } catch (pe) { log("popup failed:", pe.message); }
    return e instanceof UserError ? 2 : 1;
  }
}

// ---------------------------------------------------------------------------------------------
// CLI: no argument = MCP server. "launch" = start Otzar for agents. "doctor" = print status.
// "shortcut" = make the desktop shortcut. "launch --shortcut" = what that shortcut runs.
// ---------------------------------------------------------------------------------------------
async function cli() {
  const cmd = (process.argv.slice(2).find((a) => !a.startsWith("-")) || "").toLowerCase();
  if (!cmd || cmd === "serve") return serve();
  if (cmd === "launch" && process.argv.includes("--shortcut")) process.exit(await launchFromShortcut());
  if (cmd === "shortcut") {
    try {
      process.stdout.write(JSON.stringify(await makeShortcut(), null, 1) + "\n");
      process.exit(0);
    } catch (e) {
      process.stderr.write(e.message + "\n");
      process.exit(e instanceof UserError ? 2 : 1);
    }
  }
  if (cmd === "launch" || cmd === "doctor") {
    try {
      const r = cmd === "launch"
        ? { cdp: `${CDP_HOST}:${await ensureOtzar()}`, started_by_this_tool: state.launchedByUs }
        : await toolStatus({ coverage_check: process.argv.includes("--coverage") });
      if (state.warnings.size) r.warnings = [...state.warnings];
      process.stdout.write(JSON.stringify(r, null, 1) + "\n");
      process.exit(0);
    } catch (e) {
      process.stderr.write(e.message + "\n");
      process.exit(e instanceof UserError ? 2 : 1);
    }
  }
  process.stderr.write("usage: otzar-mcp [serve | launch | doctor [--coverage] | shortcut]\n");
  process.exit(64);
}

if (isSea || require.main === module) cli();
module.exports = { validateArgs, runSearch, checkedPath, ROUTES_GET, ROUTE_POST_OCR, findInstall, otzarProcesses, cdpVersion, ensureOtzar, deps, state, TOOLS, callTool, errorText, makeShortcut, launchFromShortcut, SHORTCUT_NAME, SHORTCUT_ARGS, START_CAP, START_WINDOW_MS, MSG_ALREADY_OPEN, UserError };
