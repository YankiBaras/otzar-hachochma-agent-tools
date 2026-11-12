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
//     get_page_image, book_info, and (IT-781) find_book, get_hit_context, list_references,
//     go_to_reference.
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
const { AsyncLocalStorage } = require("node:async_hooks");
const fs = require("node:fs");
const net = require("node:net");
const path = require("node:path");

const SERVER_NAME = "otzar-hachochma";
const SERVER_VERSION = "1.1.1";
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
// IT-781: the reader's own index (pages + tsiyunim), read as binary (zlib) and inflated in the
// page by bookData() only. Kept apart from ROUTES_GET, so the JSON apiGet() cannot use it.
const ROUTE_GET_DATA = /^api\/books\/data\/\d{1,12}$/;

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
// code (optional): a stable name for messages that the shortcut turns into a Hebrew popup (IT-788).
class UserError extends Error { constructor(message, code) { super(message); if (code) this.code = code; } }

// ---------------------------------------------------------------------------------------------
// Windows discovery: install folder, running process, CDP port
// ---------------------------------------------------------------------------------------------
// System tools by full path: the MCP host may start this server with a minimal PATH.
const SYS32 = path.join(process.env.SystemRoot || process.env.SYSTEMROOT || process.env.windir || "C:\\Windows", "System32");

// Throws if PowerShell could not run, so a failed lookup never looks like an empty answer.
// `env` passes values (paths, texts) as environment variables, so they are never part of the script.
function ps(script, env, timeout = 30_000) {
  const r = spawnSync(path.join(SYS32, "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], {
    encoding: "utf8", windowsHide: true, timeout, env: env ? { ...process.env, ...env } : process.env,
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
    // Created: start time in ms since 1970 (UTC), or null when Windows does not say. IT-788: an
    // Otzar that never answers is told apart from one that is still loading by its age.
    out = ps(`@(Get-CimInstance Win32_Process -Filter "Name='otzar.exe'" | Select-Object ProcessId,ExecutablePath,CommandLine,@{n='Created';e={ if ($_.CreationDate) { [long]([DateTimeOffset]$_.CreationDate).ToUnixTimeMilliseconds() } else { $null } }}) | ConvertTo-Json -Compress`);
  } catch (e) {
    // Fallback without command lines: enough to know whether Otzar is running at all.
    const r = spawnSync(path.join(SYS32, "tasklist.exe"), ["/FI", "IMAGENAME eq otzar.exe", "/FO", "CSV", "/NH"], { encoding: "utf8", windowsHide: true, timeout: 15_000 });
    if (r.error || r.status !== 0) {
      throw new UserError(`Cannot tell whether Otzar HaChochma is running (the process list is unavailable), so it is not started. ${e.message}`);
    }
    return String(r.stdout || "").split(/\r?\n/).filter((l) => /^"otzar\.exe"/i.test(l)).map((l) => ({ pid: Number(l.split('","')[1]), exe: null, cmd: "", created: null }));
  }
  if (!out) return [];
  let j;
  try { j = JSON.parse(out); } catch { throw new UserError("Cannot read the process list, so Otzar HaChochma is not started."); }
  return (Array.isArray(j) ? j : [j]).map((p) => ({ pid: p.ProcessId, exe: p.ExecutablePath, cmd: p.CommandLine || "",
    created: Number.isFinite(p.Created) ? p.Created : null }));
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

// --- the Otzar data disk (IT-788) ---
// Otzar's own starter (STARTER_API.getOtzarDrives in app.asar, update 7.40001) looks for its data
// disk on every drive: a drive holds it if <drive>\data\otzar_ver.txt or
// <drive>\otzarDisk\data\otzar_ver.txt exists. In network mode (onet.otzar or onet.otzar.tmp in
// the otzarLocal install folder) no disk is needed. Without the disk the starter shows its own
// box ("no Otzar disk connected") and the debugging port never answers, so the tool used to wait
// on it as "still loading" forever. This check uses the same rule, read-only: it only tests
// whether these files exist.
// Returns (a promise of) { state: "present" | "missing" | "network" | "unknown" | "skipped", drives?,
// skipped_drives?, reason? }. Only "missing" blocks a start.
// Like the starter's checkDriveWithDeadLine, every drive has its own time limit (IT-788 review r1):
// a drive that does not answer in DISK_PROBE_MS is skipped, and the others are still checked.
// The first marker found gives "present" at once, even while another drive still hangs. Every
// drive answered or skipped and no marker: "missing". "unknown" (never blocks) is kept for a check
// that could not run: no otzarLocal folder, no drive list, or a probe that could not be started.
const DISK_PROBE_MS = 5_000;
const DISK_MARKERS = ["data\\otzar_ver.txt", "otzarDisk\\data\\otzar_ver.txt"];
function otzarLocalDir(exe) {
  let d = exe ? path.dirname(exe) : "";
  for (let i = 0; i < 8 && d && path.dirname(d) !== d; i++) {
    if (/^otzarLocal$/i.test(path.basename(d))) return d;
    d = path.dirname(d);
  }
  return null;
}
// The drive letters ("C:", ...). GetDrives() only lists them; it does not touch any drive, so one
// dead drive cannot stall it. Throws when the list cannot be read.
function driveLetters() {
  return ps(`[IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 2) }`, null, 15_000)
    .split(/\r?\n/).map((d) => d.trim().toUpperCase()).filter((d) => /^[A-Z]:$/.test(d));
}
// Does one drive hold a marker? Its own cmd.exe child, so a drive that hangs blocks only that
// child: after `ms` the answer is "timeout" and the child is left to Windows (killed, best effort).
// Resolves "yes" | "no" | "timeout" | "error". The letter is checked here; the markers are constants.
function probeDrive(letter, ms) {
  if (!/^[A-Z]:$/.test(letter)) return Promise.resolve("error");
  const test = DISK_MARKERS.map((x) => `if exist "${letter}\\${x}" (echo YES)`).join(" else ") + " else (echo NO)";
  return new Promise((ok) => {
    let done = false, out = "";
    const finish = (r) => { if (!done) { done = true; clearTimeout(t); ok(r); } };
    let child;
    try {
      child = spawn(path.join(SYS32, "cmd.exe"), ["/d", "/s", "/c", `"${test}"`], { windowsHide: true, windowsVerbatimArguments: true, stdio: ["ignore", "pipe", "ignore"] });
    } catch { return ok("error"); }
    const t = setTimeout(() => { try { child.kill(); } catch {} finish("timeout"); }, ms);
    child.stdout.on("data", (d) => { out += d; });
    child.on("error", () => finish("error"));
    child.on("close", (code) => finish(/\bYES\b/.test(out) ? "yes" : /\bNO\b/.test(out) && code === 0 ? "no" : "error"));
  });
}
// The scan policy (pure, tested offline with a fake probe). All drives are probed at once, each
// with its own limit; the first "yes" wins without waiting for the rest.
function scanDrives(drives, probe, ms = DISK_PROBE_MS) {
  return new Promise((ok) => {
    if (!drives.length) return ok({ state: "unknown", reason: "no drives were listed" });
    let left = drives.length, settled = false;
    const skipped = [], failed = [];
    const one = (d) => Promise.race([
      Promise.resolve().then(() => probe(d, ms)).catch(() => "error"),
      new Promise((r) => setTimeout(() => r("timeout"), ms + 500).unref()),
    ]);
    for (const d of drives) {
      one(d).then((r) => {
        if (settled) return;
        if (r === "yes") { settled = true; return ok({ state: "present", drives: [d] }); }
        if (r === "timeout") skipped.push(d);
        else if (r !== "no") failed.push(d);
        if (--left) return;
        settled = true;
        if (failed.length) ok({ state: "unknown", reason: `the check could not run on ${failed.join(", ")}`, skipped_drives: skipped });
        else ok({ state: "missing", ...(skipped.length ? { skipped_drives: skipped } : {}) });
      });
    }
  });
}
async function otzarDiskState(inst) {
  if (process.env.OTZAR_MCP_SKIP_DISK_CHECK === "1") return { state: "skipped", reason: "OTZAR_MCP_SKIP_DISK_CHECK=1" };
  const local = otzarLocalDir(inst && inst.exe);
  if (!local) return { state: "unknown", reason: "the otzarLocal install folder was not found, so network mode cannot be checked" };
  if (["onet.otzar", "onet.otzar.tmp"].some((f) => fs.existsSync(path.join(local, f)))) return { state: "network" };
  let drives;
  try { drives = driveLetters(); } catch (e) { return { state: "unknown", reason: `the drive list could not be read (${e.message.slice(0, 120)})` }; }
  return scanDrives(drives, probeDrive, DISK_PROBE_MS);
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
// It stays until the user presses OK (Popup's own timeout 0 = no timeout). The 30 s limit of
// other PowerShell calls would close it by itself (IT-779 review N1), so this call gets a long
// limit instead: only a box nobody answers for POPUP_TIMEOUT_MS is closed.
const POPUP_TIMEOUT_MS = 12 * 60 * 60_000;
function popup(text) {
  // 48 warning icon + 4096 system modal + 0x80000 right-aligned + 0x100000 RTL reading order.
  ps(`[void](New-Object -ComObject WScript.Shell).Popup($env:OTZAR_MCP_TEXT, 0, $env:OTZAR_MCP_TITLE, 48 + 4096 + 0x80000 + 0x100000)`,
    { OTZAR_MCP_TEXT: text, OTZAR_MCP_TITLE: SHORTCUT_NAME }, POPUP_TIMEOUT_MS);
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
  startLockName: () => startLockName(),
  diskState: (inst) => otzarDiskState(inst),
};

// --- start lock (IT-779 review N2) ---
// Every otzar-mcp process (the MCP server, "launch", each shortcut double-click) takes this lock
// before it looks for Otzar and maybe starts it, and keeps it until the started Otzar answers.
// So two launches close together cannot both see "not running" and start two Otzars. The lock
// is a named pipe of this user: Windows lets only one process create it, and frees it when that
// process ends (no stale lock file). It never accepts a connection (maxConnections 0) and is not
// a network port.
function startLockName() {
  const u = String(process.env.USERNAME || "user").replace(/[^A-Za-z0-9_.-]/g, "_").slice(0, 40);
  return `\\\\.\\pipe\\otzar-mcp-start-${u}`;
}
function tryLock(name) {
  return new Promise((ok) => {
    const srv = net.createServer((c) => c.destroy());
    srv.maxConnections = 0;
    srv.once("error", () => ok(null));
    srv.listen(name, () => ok(srv));
  });
}
const lockCtx = new AsyncLocalStorage();
async function withStartLock(fn) {
  if (lockCtx.getStore()) return fn(); // nested call inside this process's own locked section
  const name = deps.startLockName();
  const deadline = Date.now() + LAUNCH_WAIT_MS + 15_000;
  let srv;
  while (!(srv = await tryLock(name))) {
    if (Date.now() > deadline) {
      throw new UserError("Another start of Otzar HaChochma (from the shortcut or another agent) is still running. Wait a minute and call the tool again.");
    }
    await sleep(500);
  }
  try { return await lockCtx.run(true, fn); } finally { srv.close(); }
}

// The debugging ports on otzar.exe command lines (exact numbers: IT-779 review N3).
function cmdPorts(procs) {
  const out = [];
  for (const p of procs) {
    const m = /--remote-debugging-port=(\d{1,5})(?!\d)/.exec(p.cmd || "");
    if (m && !out.includes(Number(m[1]))) out.push(Number(m[1]));
  }
  return out;
}

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

const MSG_STILL_LOADING =
  "Otzar HaChochma is starting (it was opened from the shortcut or by another agent), but its agent connection does not answer yet. " +
  "Wait half a minute and call the tool again. If it still fails, tell the user: check Otzar on the screen and close it if it shows an error.";

// IT-788: no data disk -> no start.
const MSG_NO_DISK =
  "The Otzar HaChochma data disk is not connected to this PC (no drive has data\\otzar_ver.txt), so Otzar was not started. " +
  "Tell the user: connect the Otzar disk and try again. Do not retry until the user has done this.";
// IT-788: an Otzar with a debugging port that still does not answer this long after it started is
// not loading any more. The usual cause is a missing data disk (the starter waits on its own box,
// or the disk's protection layer hangs the process so that it cannot even be ended).
const STUCK_AFTER_MS = LAUNCH_WAIT_MS + 60_000;
async function notAnsweringError(exe, ageMs) {
  const disk = await deps.diskState(exe ? { exe } : null);
  const mins = Math.max(1, Math.round(ageMs / 60_000));
  return new UserError(
    `Otzar HaChochma was started about ${mins} minute(s) ago, but it does not answer on its agent connection: it is not loading any more, it is stuck. ` +
    (disk.state === "missing" ? "The Otzar data disk is not connected to this PC, which is the likely cause. " : "") +
    "Tell the user: check that the Otzar disk is connected; if Otzar shows a message or an error, close it. " +
    `If Otzar cannot be closed (it stays in Task Manager), restart the PC. Then open Otzar ${SHORTCUT_HINT}. ` +
    "Nothing was closed or changed. Do not retry until the user has done this.",
    disk.state === "missing" ? "not_answering_no_disk" : "not_answering");
}

// Makes sure Otzar runs with a CDP port and returns the port. Starts Otzar if it isn't running
// (unless OTZAR_MCP_NO_LAUNCH=1). Never kills or restarts it.
// waitForWindow=false returns as soon as the debugging port answers (used by the shortcut).
// loadingWaitMs: how long to wait for an Otzar that was opened with a debugging port (by the
// shortcut or another otzar-mcp process) but does not answer yet (IT-779 review N2).
async function ensureOtzar({ waitForWindow = true, loadingWaitMs = Math.min(20_000, LAUNCH_WAIT_MS) } = {}) {
  if (state.port && (await cdpVersion(state.port))) return connected(state.port);
  state.port = null;
  const envPort = process.env.OTZAR_CDP_PORT;
  if (envPort) {
    const p = int(envPort, "OTZAR_CDP_PORT", 1, 65535);
    if (await cdpVersion(p)) return connected(p);
  }
  return withStartLock(() => connectOrStart({ waitForWindow, loadingWaitMs }));
}

async function connectOrStart({ waitForWindow, loadingWaitMs }) {
  const procs = deps.otzarProcesses();
  if (procs.length) {
    const ports = cmdPorts(procs);
    for (const port of ports) if (await cdpVersion(port)) return connected(port);
    if (ports.length) {
      // IT-788: how long ago the newest Otzar with a debugging port was started (null: unknown).
      // Past STUCK_AFTER_MS it is no longer "still loading": it does not answer at all.
      const created = procs.filter((p) => cmdPorts([p]).length && Number.isFinite(p.created)).map((p) => p.created);
      const ageAt0 = created.length ? Math.max(0, deps.now() - Math.max(...created)) : null;
      const exe = (procs.find((p) => p.exe) || {}).exe || null;
      if (ageAt0 != null && ageAt0 >= STUCK_AFTER_MS) throw await notAnsweringError(exe, ageAt0);
      const ours = state.lastStartPort != null && ports.includes(state.lastStartPort);
      if (ours && state.noAnswer) {
        throw new UserError(
          "Otzar HaChochma, started by this tool, is running but its agent connection does not answer (it is still loading, or it shows an error). " +
          "Wait half a minute and call the tool again. If it still fails, tell the user: check that the Otzar disk is connected, and check Otzar on the screen and close it if it shows an error.");
      }
      // Opened with a debugging port by someone else (the shortcut, another agent): it is still
      // loading. Wait for it a little instead of telling the user to close it, but never past the
      // point where it counts as not answering.
      const wait = ageAt0 == null ? loadingWaitMs : Math.min(loadingWaitMs, Math.max(1000, STUCK_AFTER_MS - ageAt0));
      const t0 = Date.now();
      while (Date.now() - t0 < wait) {
        await sleep(1000);
        for (const port of ports) {
          if (await cdpVersion(port)) {
            connected(port);
            if (waitForWindow) await waitReady(Math.max(5000, LAUNCH_WAIT_MS - (Date.now() - t0)));
            return port;
          }
        }
      }
      if (ageAt0 != null && ageAt0 + (Date.now() - t0) >= STUCK_AFTER_MS) throw await notAnsweringError(exe, ageAt0 + (Date.now() - t0));
      throw new UserError(MSG_STILL_LOADING);
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
  // IT-788: without its data disk Otzar never comes up (and may not be closable). Do not start it.
  // Not a start, so it does not count for the cap and does not set noAnswer.
  const disk = await deps.diskState(inst);
  if (disk.state === "missing") throw new UserError(MSG_NO_DISK, "no_disk");
  if (disk.state === "unknown") log("disk check skipped:", disk.reason);
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
    "If Otzar is still loading, call the tool again in a minute. Otherwise tell the user: check that the Otzar disk is connected, and check Otzar on the screen and close it if it shows an error. " +
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
  if (!page) {
    // IT-788: Otzar's starter may be up (the port answers) but waiting on its own "no Otzar disk"
    // box, so the main window never comes. Say so instead of "still loading" every time.
    let exe = state.install ? state.install.exe : null;
    try { if (!exe) exe = (deps.otzarProcesses().find((p) => p.exe) || {}).exe || null; } catch {}
    if ((await deps.diskState(exe ? { exe } : null)).state === "missing") {
      throw new UserError(
        "Otzar HaChochma is open, but its data disk is not connected, so its main window cannot load. " +
        `Tell the user: connect the Otzar disk; if Otzar shows that no disk was found, press "נסה שוב" (try again) there, or close Otzar and open it again ${SHORTCUT_HINT}. ` +
        "Do not retry until the user has done this.", "open_no_disk");
    }
    throw new UserError("Otzar's main window is not available yet (still loading?). Try again in a few seconds.");
  }
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
  const __webpackReq = () => {
    const K = Symbol.for('otzar-mcp.webpackRequire');
    let req = globalThis[K] || null;
    if (!req) {
      const id = 'otzar_mcp_' + Date.now();
      try { globalThis.webpackJsonp.push([[id], { [id]: (m, e, r) => { req = r; } }, [[id]]]); } catch { return null; }
      if (req) Object.defineProperty(globalThis, K, { value: req, enumerable: false });
    }
    return req && req.c ? req : null;
  };
  // A function exported by one of Otzar's webpack modules, found by its export name (module ids
  // and minified names change with every Otzar build).
  const __exportFn = (name) => {
    const req = __webpackReq(); if (!req) return null;
    const k = Object.keys(req.c).find((k) => req.c[k] && req.c[k].exports && typeof req.c[k].exports[name] === 'function');
    if (!k) return null;
    const e = req.c[k].exports;
    return (...a) => e[name](...a);
  };
  // The exports of the first module that exports all these names as functions (IT-788: the
  // compression library Otzar's own reader uses, found as inflate + inflateRaw + ungzip).
  const __exportsWith = (names) => {
    const req = __webpackReq(); if (!req) return null;
    const k = Object.keys(req.c).find((k) => { const e = req.c[k] && req.c[k].exports; return e && names.every((n) => typeof e[n] === 'function'); });
    return k ? req.c[k].exports : null;
  };
`;

function apiGet(s, p) {
  const route = checkedPath(p, ROUTES_GET);
  return s.evaluate(`(async () => { const r = await globalThis.AXIOS_OFFLINE.get(${JSON.stringify(route)}); return r.data; })()`);
}

// IT-781: the reader's index of a book: GET api/books/data/<id>, a compressed JSON array
// [bookInfo, pages, tsiyunim, mareiMekomot]. It is decoded and parsed inside Otzar's page, and
// only a trimmed copy comes back over CDP: refs as [title, page, type], non-empty pages as
// [position, label]. Matching is done here (pure functions below, tested offline).
//
// IT-788: decoded the way Otzar's own reader does it, so it works on every disk version. Otzar's
// getAllData() (module "bf84", update 7.40001) GETs the same route through the same Axios object
// (under Electron, c.Axios is globalThis.AXIOS_OFFLINE), then: try { r = inflate(r) } catch {};
// r = new TextDecoder().decode(r); JSON.parse(r). Its inflate() is pako's, which detects zlib and
// gzip by itself. getAllData() itself is not called: it also writes to the reader tab's store
// (setTxtBook). The GET stays here, on the fixed route allow-list; only the decoder is Otzar's.
// The decoder is found by export names (inflate + inflateRaw + ungzip), never by module id.
// If it is missing, our own decoders follow: zlib, gzip, raw deflate, plain JSON. A body that none
// of them reads gives a UserError with diagnostics (first bytes, length, what was tried, disk
// version), never a raw SyntaxError.
const BOOK_DATA_PAGE_FN = `async (route) => {
  ${PAGE_HELPERS}
  const diag = { tried: [] };
  try { const lic = __root().$store.state.offline.licence; diag.otzar_disk_version = lic.otzarVer ?? null; } catch { diag.otzar_disk_version = null; }
  let r;
  try { r = await globalThis.AXIOS_OFFLINE.get(route, { responseType: 'arraybuffer' }); }
  catch (e) { return { missing: true, diag: { ...diag, get_error: String(e && e.message || e).slice(0, 200) } }; }
  let body = r ? r.data : null, parsed = null;
  diag.data_type = body == null ? String(body) : ArrayBuffer.isView(body) ? body.constructor.name : body instanceof ArrayBuffer ? 'ArrayBuffer' : Array.isArray(body) ? 'array' : typeof body;
  let u8 = null;
  if (body instanceof ArrayBuffer) u8 = new Uint8Array(body);
  else if (ArrayBuffer.isView(body)) u8 = new Uint8Array(body.buffer, body.byteOffset, body.byteLength);
  else if (Array.isArray(body)) parsed = body; // already JSON (Axios parsed it)
  else if (typeof body === 'string') {
    // A string body: JSON text, or binary as one char per byte (what pako also accepts).
    if (/^\\s*\\[/.test(body)) { try { parsed = JSON.parse(body); diag.decoder = 'string-json'; } catch {} }
    if (!parsed) u8 = [...body].every((c) => c.charCodeAt(0) < 256) ? Uint8Array.from(body, (c) => c.charCodeAt(0)) : new TextEncoder().encode(body);
  } else return { missing: true, diag };
  if (u8) {
    diag.length = u8.byteLength;
    diag.head_hex = [...u8.subarray(0, 16)].map((b) => b.toString(16).padStart(2, '0')).join(' ');
    if (!u8.byteLength) return { missing: true, diag };
    if (u8.byteLength > 64 * 1024 * 1024) return { tooLarge: true, diag };
    const parse = (bytes) => { let t = new TextDecoder().decode(bytes); if (t.charCodeAt(0) === 0xfeff) t = t.slice(1); const j = JSON.parse(t); if (!Array.isArray(j)) throw new Error('not an array'); return j; };
    const stream = async (fmt) => new Uint8Array(await new Response(new Blob([u8]).stream().pipeThrough(new DecompressionStream(fmt))).arrayBuffer());
    const z = __exportsWith(['inflate', 'inflateRaw', 'ungzip']);
    diag.otzar_decoder_found = !!z;
    const attempts = [];
    // 1. Exactly Otzar's own steps.
    if (z) attempts.push(['otzar', async () => { let x = u8; try { x = z.inflate(x); } catch {} return parse(x); }]);
    if (z) attempts.push(['otzar-inflateRaw', async () => parse(z.inflateRaw(u8))]);
    // 2. Our own decoders (fallback when Otzar's decoder is not found or does not read it).
    if (u8[0] === 0x1f && u8[1] === 0x8b) attempts.push(['gzip', async () => parse(await stream('gzip'))]);
    if ((u8[0] & 0x0f) === 8 && ((u8[0] << 8) | u8[1]) % 31 === 0) attempts.push(['zlib', async () => parse(await stream('deflate'))]);
    attempts.push(['plain', async () => parse(u8)]);
    attempts.push(['deflate-raw', async () => parse(await stream('deflate-raw'))]);
    for (const [name, fn] of attempts) {
      try { parsed = await fn(); diag.decoder = name; break; }
      catch (e) { diag.tried.push(name + ': ' + String(e && e.message || e).slice(0, 80)); }
    }
  }
  if (!parsed) return { decodeFailed: true, diag };
  const ts = Array.isArray(parsed[2]) ? parsed[2] : [], pg = Array.isArray(parsed[1]) ? parsed[1] : [];
  const info = Array.isArray(parsed[0]) ? parsed[0][0] : null;
  return {
    refs: ts.filter((t) => t && typeof t.title === 'string').map((t) => [t.title, Number(t.page), Number(t.type)]),
    pages: pg.filter((p) => p && !p.empty).map((p) => [Number(p.position), String((p.pagedata && p.pagedata[0] && p.pagedata[0].letter) || '')]),
    isText: !!(info && info.moredata && info.moredata.isText == 1),
    diag,
  };
}`;

async function bookData(s, book) {
  const route = checkedPath(`api/books/data/${int(book, "book_id", 1)}`, [ROUTE_GET_DATA]);
  let d;
  try { d = await s.evaluate(`(${BOOK_DATA_PAGE_FN})(${JSON.stringify(route)})`, 90_000); }
  catch (e) {
    // An exception inside the page is a format problem, reported with diagnostics. A lost CDP
    // connection is not: errorText() turns that into "Otzar was closed".
    if (/^CDP /.test(e.message)) throw e;
    throw bookDataError(book, { tried: [`page: ${String(e.message).slice(0, 120)}`] });
  }
  if (!d || d.missing) throw new UserError(`Book ${book} was not found in this library (license packages differ between users).`);
  if (d.tooLarge) throw new UserError(`The index of book ${book} is too large to read.`);
  if (d.decodeFailed) throw bookDataError(book, d.diag);
  // A text book (moredata.isText) has no page list in this route; Otzar's reader reads its pages
  // from api/books/<id>/pages instead (already on the allow-list).
  if ((!Array.isArray(d.pages) || !d.pages.length) && d.isText) {
    d.pages = (await pagesOf(s, book)).map((p) => [Number(p.position), String(p.pagedata?.[0]?.letter || "")]);
  }
  if (!Array.isArray(d.pages) || !d.pages.length) throw new UserError(`Book ${book} has no pages, or it is not in this library (license packages differ between users).`);
  return d;
}

// "otzar-mcp.exe doctor --book <id>" (IT-788): how this PC's Otzar returns a book's index, and
// whether this tool reads it. Read-only: the same single GET as go_to_reference. Only sizes, the
// first 16 bytes and counts are printed, never the book's content.
async function indexCheck(book) {
  const route = checkedPath(`api/books/data/${int(book, "book_id", 1)}`, [ROUTE_GET_DATA]);
  return withSession(async (s) => {
    const d = await s.evaluate(`(${BOOK_DATA_PAGE_FN})(${JSON.stringify(route)})`, 90_000);
    const out = { book_id: book, ok: !!(d && d.refs), ...(d && d.diag) };
    if (d && d.refs) Object.assign(out, { references: d.refs.length, pages: d.pages.length, text_book: d.isText });
    else out.result = !d || d.missing ? "not found in this library" : d.tooLarge ? "too large" : "format not readable";
    return out;
  });
}

// The message for an index this tool cannot read (IT-788). It is meant to be sent to the developer
// as is; "otzar-mcp.exe doctor --book <id>" prints the same details.
function bookDataError(book, diag = {}) {
  const parts = [
    `disk version ${diag.otzar_disk_version ?? "unknown"}`,
    `data type ${diag.data_type ?? "unknown"}`,
    `length ${diag.length ?? "unknown"}`,
    `first bytes [${diag.head_hex ?? ""}]`,
    `Otzar's decoder ${diag.otzar_decoder_found ? "found" : "not found"}`,
    `tried: ${(diag.tried || []).join("; ") || "nothing"}`,
  ];
  return new UserError(
    `Could not read the index (tsiyunim) of book ${book}: this Otzar version returns it in a format this tool does not know. ` +
    "go_to_reference and list_references cannot be used on this PC until the tool is updated; get_page_text with page_label still works. " +
    `Tell the user to send this message to the otzar-mcp developer (or run: otzar-mcp.exe doctor --book ${book}). Details: ${parts.join(", ")}.`,
    "book_data_format");
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
// bookIds (IT-781): optional list of validated book ids. The search then runs through the search
// component's own doFreeSearch(text, ids), the same call Otzar's scope combo makes. It is never
// sent on the socket directly, and never as an in-book search (that opens a book tab).
async function doSearch(query, limit, timeoutSec, bookIds = null) {
  if (typeof query !== "string" || !query.trim()) throw new UserError("query is required");
  if (query.length > 300 || /[\u0000-\u001f\u007f]/.test(query)) throw new UserError("query must be one line, up to 300 characters");
  const q = query.trim();
  const lim = Number(limit);
  const ids = bookIds && bookIds.length ? bookIds.map((x) => int(x, "book_ids", 1)) : null;
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
        const scope = ${JSON.stringify(ids)};
        if (scope) { const set = new Set(scope); st.results.outside_scope = list.filter((b) => !set.has(b.id)).length; }
        return st; })()`),
      submit: async () => {
        if (ids) {
          // Scoped search: the component's own method with the book ids. In Otzar's "special"
          // search type the list is ignored (it would search the whole library), so refuse it.
          const r = await s.evaluate(`(() => { ${PAGE_HELPERS} const c = __fsComp(); const r = __root();
            if (!c || typeof c.doFreeSearch !== 'function') return 'nocomp';
            if (r.$store.state.freeSearch.searchType === 'special') return 'special';
            c.doFreeSearch(${JSON.stringify(q)}, ${JSON.stringify(ids)}); return 'ok'; })()`);
          if (r === "special") throw new UserError("Otzar's search is set to the special search type, which cannot be limited to chosen books. Search without book_ids, or ask the user to switch Otzar back to the regular search.");
          if (r !== "ok") throw new UserError("Otzar's search panel was not found. Make sure the main window finished loading.");
          return;
        }
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
    const res = st.results;
    if (ids) {
      const outside = res.outside_scope || 0;
      delete res.outside_scope;
      res.scope = { book_ids: ids, count: ids.length };
      if (outside) res.scope_warning = `${outside} book(s) in the results are not in book_ids: Otzar did not limit this search. Treat it as a search of the whole library.`;
    }
    return res;
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

// OCR words of one window [start, start + n) of one page part (pagedata entry), n <= OCR_CHUNK.
// startWord is an absolute word index in the book; the route clips the request to this part's
// [firstWord, firstWord + numWords) window and returns at most ~200 words. Some indices have no
// word (graphics, punctuation), so positions:true is asked for as well: it returns the absolute
// index of every returned word, which is the same numbering as the search hit offsets.
async function ocrRange(s, book, page, pd, start, n) {
  const pageId = int(pd.pageId ?? page.id, "pageId");
  const route = checkedPath(`api/books/${book}/pages/${pageId}/ocr`, [ROUTE_POST_OCR]);
  const total = int(pd.numWords || 0, "numWords");
  const first = int(pd.firstWord || 0, "firstWord");
  const base = { x: 0, y: 0, width: int(pd.width || 0, "width"), height: int(pd.height || 0, "height"),
    startWord: int(start, "startWord"), numWord: int(n, "numWord", 1, OCR_CHUNK), search: [],
    pageData: { id: int(pd.id, "id"), width: int(pd.width || 0, "width"), height: int(pd.height || 0, "height"),
      margin: Number(pd.margin) || 0, firstWord: first, numWords: total,
      letter: String(pd.letter ?? ""), pageId, userId: Number(pd.userId) || 0 } };
  const [words, idx] = await s.evaluate(`(async () => {
    const a = globalThis.AXIOS_OFFLINE, u = ${JSON.stringify(route)}, b = ${JSON.stringify(base)};
    const t = await a.post(u, Object.assign({}, b, { positions: false }));
    const q = await a.post(u, Object.assign({}, b, { positions: true }));
    return [t.data, q.data]; })()`);
  const out = [];
  if (!Array.isArray(words)) return out;
  const pos = Array.isArray(idx) ? idx.map(Number) : [];
  const list = words.map(String);
  if (list.length === pos.length + 1 && list[list.length - 1] === "") list.pop(); // empty terminator
  list.forEach((w, i) => { if (w !== "") out.push({ w, abs: list.length === pos.length ? pos[i] : null }); });
  return out;
}

async function pageWords(s, book, page) {
  const out = [];
  for (const pd of page.pagedata || []) {
    const total = int(pd.numWords || 0, "numWords");
    const first = int(pd.firstWord || 0, "firstWord");
    for (let start = first; start < first + total; start += OCR_CHUNK) {
      out.push(...(await ocrRange(s, book, page, pd, start, Math.min(OCR_CHUNK, first + total - start))));
    }
  }
  return out;
}

// --- hits of the current search in one book (shared by search_hits and get_hit_context) ---
async function currentHits(s, book) {
  const fsRef = await s.evaluate(`(() => { ${PAGE_HELPERS} const f = __fsStore(); if (!f) return null;
    const b = (f.getCurrentList?.booksList || []).find((x) => x.id === ${book});
    return b && b.fs ? { fs: String(b.fs.fs), start: b.fs.start, end: b.fs.end, results: b.fs.results, query: f.getFinalTextSearch } : { query: f.getFinalTextSearch }; })()`);
  if (!fsRef || !fsRef.fs) throw new UserError(`Book ${book} is not in the results of the current search${fsRef?.query ? ` ("${fsRef.query}")` : ""}. Run search first.`);
  if (!/^[A-Za-z0-9]{1,128}$/.test(fsRef.fs)) throw new Error("internal: unexpected search-file token");
  const hits = await apiGet(s, `api/freesearch/words/search/${fsRef.fs}/${int(fsRef.start, "start")}/${int(fsRef.end, "end")}/${book}`);
  // Hit offsets are 1-based word numbers; OCR positions and page firstWord are 0-based.
  const offsets = [...new Set((Array.isArray(hits) ? hits : []).map(Number).filter((x) => Number.isInteger(x) && x > 0))]
    .map((x) => x - 1).sort((a, b) => a - b);
  return { fsRef, offsets };
}

// Words of one phrase hit are consecutive offsets: group offsets less than 4 words apart.
function groupOffsets(offs) {
  const groups = [];
  for (const off of offs) {
    const g = groups[groups.length - 1];
    if (g && off - g[g.length - 1] < 4) g.push(off); else groups.push([off]);
  }
  return groups;
}

// The page parts of a book in word order: one segment per pagedata entry.
function pageSegments(pages) {
  const segs = [];
  for (const p of pages) for (const pd of p.pagedata || []) {
    const first = Number(pd.firstWord), n = Number(pd.numWords);
    if (Number.isInteger(first) && Number.isInteger(n) && n > 0) segs.push({ page: p, pd, first, end: first + n });
  }
  return segs.sort((a, b) => a.first - b.first);
}

// Plans the OCR reads for the text around one hit: [g[0] - win, g[last] + win], over the part
// that holds the hit and at most one part before and after it (the neighbouring page). Returns
// { seg (index of the hit's part), ranges: [{ seg, start, n }] (n <= OCR_CHUNK), cut } where cut
// says the window was shortened at a page edge (a neighbouring part too short to fill it). The
// book's own start and end are not a cut.
function planHitWindow(segs, g, win) {
  const i = segs.findIndex((x) => g[0] >= x.first && g[0] < x.end);
  if (i < 0) return null;
  let j = segs.findIndex((x) => g[g.length - 1] >= x.first && g[g.length - 1] < x.end);
  if (j < i) j = i;
  const lo = g[0] - win, hi = g[g.length - 1] + win + 1; // [lo, hi)
  const from = Math.max(0, i - 1), to = Math.min(segs.length - 1, j + 1);
  const ranges = [];
  for (let k = from; k <= to; k++) {
    const a = Math.max(lo, segs[k].first), b = Math.min(hi, segs[k].end);
    for (let st = a; st < b; st += OCR_CHUNK) ranges.push({ seg: k, start: st, n: Math.min(OCR_CHUNK, b - st) });
  }
  const covLo = ranges.length ? ranges[0].start : g[0];
  const last = ranges[ranges.length - 1];
  const covHi = last ? last.start + last.n : g[g.length - 1] + 1;
  // The book's own start and end are not a cut: there are no words beyond them.
  return { seg: i, ranges, cut: covLo > Math.max(lo, segs[0].first) || covHi < Math.min(hi, segs[segs.length - 1].end) };
}

// Joins the words of a planned window: hit words in «», a page change marked with ‖.
function hitText(parts, g) {
  const lo = g[0], hi = g[g.length - 1];
  const words = [];
  let prevPage = null;
  for (const { page, list } of parts) {
    if (prevPage && page !== prevPage && list.length) words.push({ w: "‖", mark: false });
    if (list.length) prevPage = page;
    for (const x of list) words.push({ w: x.w, abs: x.abs, mark: x.abs != null && x.abs >= lo && x.abs <= hi });
  }
  if (!words.some((x) => x.mark)) {
    // No exact position (an index without a word): mark the first word at or after the hit.
    const k = words.findIndex((x) => x.abs != null && x.abs >= lo);
    if (k >= 0) words[k].mark = true;
  }
  return words.map((x) => (x.mark ? `«${x.w}»` : x.w)).join(" ");
}

async function toolGetHitContext(args) {
  const book = int(args.book_id, "book_id", 1);
  const win = int(args.window ?? 15, "window", 1, 100);
  const maxHits = int(args.max_hits ?? 10, "max_hits", 1, 50);
  const offset = int(args.offset ?? 0, "offset", 0, 1e7);
  return withSession(async (s) => {
    const { fsRef, offsets } = await currentHits(s, book);
    const groups = groupOffsets(offsets);
    const pages = await pagesOf(s, book);
    const segs = pageSegments(pages);
    const label = (p) => p.pagedata?.[0]?.letter || null;
    const out = [];
    for (const g of groups.slice(offset, offset + maxHits)) {
      const plan = planHitWindow(segs, g, win);
      if (!plan) continue;
      const parts = [];
      for (const r of plan.ranges) {
        const seg = segs[r.seg];
        parts.push({ page: seg.page, list: await ocrRange(s, book, seg.page, seg.pd, r.start, r.n) });
      }
      const home = segs[plan.seg].page;
      const text = hitText(parts, g);
      const item = { page: home.position, page_label: label(home), text: text || "(no OCR text here)" };
      // The window reached into the previous or next page (marked ‖ in the text).
      const others = [...new Set(parts.filter((x) => x.list.length).map((x) => x.page))].filter((p) => p !== home);
      if (others.length) item.other_pages = others.map((p) => ({ page: p.position, page_label: label(p) }));
      if (plan.cut) item.cut_at_page_edge = true;
      out.push(item);
    }
    const next = offset + maxHits < groups.length ? offset + maxHits : null;
    return { query: fsRef.query, book_id: book, results_in_book: fsRef.results, total_hits: groups.length,
      offset, returned: out.length, next_offset: next, window: win, hits: out };
  });
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
    const { fsRef, offsets } = await currentHits(s, book);
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
      const groups = groupOffsets(offs);
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
      dataUrl = await s.evaluate(`(async () => { ${PAGE_HELPERS}
        const f = __exportFn('getImageAsDataUrl');
        if (!f) return null;
        return await f(${book}, ${JSON.stringify(imgPageId)}, { resize: ${width} });
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

// ---------------------------------------------------------------------------------------------
// IT-781: find a book by name (Otzar's F2 search, in memory)
// ---------------------------------------------------------------------------------------------
// Catalogue names usually leave these out ("שו"ת אגרות משה" finds a wrong book, "אגרות משה" works).
const BOOK_PREFIX_RE = /^(?:שו["״']?ת|ספר)\s+/;

// Calls Otzar's exported searchByTxt(text, special=true, state) with a THROWAWAY state object:
// searchByTxt writes tsiyunSearch and hiddenBooks into the state it gets, so the real
// store.state.bookList is never passed. It runs in memory on Otzar's book index: no API call,
// no UI change, no history. Never falls back to bookList/setSearchTo (that filters the user's list).
async function findBooksOnce(s, text, limit) {
  const r = await s.evaluate(`(() => { ${PAGE_HELPERS}
    const f = __exportFn('searchByTxt'); const root = __root();
    if (!f || !root || !root.$store) return { missing: true };
    const real = root.$store.state.bookList || {};
    let cfg = real.searchResultConfig;
    try { cfg = JSON.parse(JSON.stringify(cfg)); } catch {}
    const tmp = { searchResultConfig: cfg };
    const res = f(${JSON.stringify(text)}, true, tmp) || [];
    const list = Array.isArray(res) ? res : [];
    const books = list.slice(0, ${int(limit, "limit", 1, 200)}).map((x) => {
      const b = (x && x.book) || x || {};
      const authors = []; for (let i = 0; i < (b.authors_length || 0); i++) if (b['authors_' + i + '_name']) authors.push(b['authors_' + i + '_name']);
      const vols = Number(b.volumes_length) || 0;
      const isMain = !b.mainVolume || b.mainVolume === b.id;
      const volumeIds = []; if (isMain && vols > 1) for (let i = 0; i < vols; i++) if (b['volumes_' + i + '_id']) volumeIds.push(b['volumes_' + i + '_id']);
      return { book_id: b.id ?? x.id, name: b.name, volume: b.volume || undefined,
        main_volume_id: isMain ? undefined : b.mainVolume,
        volumes_in_set: vols > 1 ? vols : undefined, volume_ids: volumeIds.length ? volumeIds : undefined,
        all_volumes_matched: x.allVolumes ? true : undefined,
        authors: authors.length ? authors : undefined, place: b.places || undefined,
        year: b.fromyear ? (b.toyear && b.toyear !== b.fromyear ? b.fromyear + '-' + b.toyear : b.fromyear) : undefined };
    });
    return { total: list.length, hidden: Number(tmp.hiddenBooks) || 0, ref: typeof tmp.tsiyunSearch === 'string' ? tmp.tsiyunSearch : '', books };
  })()`);
  if (!r || r.missing) throw new UserError("Otzar's book-name search was not found in this Otzar version. Update otzar-mcp, or use search with the book's name.");
  return r;
}

async function toolFindBook(args) {
  const query = String(args.query).replace(/^\s+|\s+$/g, ""); // inner double spaces are syntax: keep them
  if (!query) throw new UserError("query is required");
  const limit = int(args.limit ?? 20, "limit", 1, 200);
  return withSession(async (s) => {
    let used = query;
    let r = await findBooksOnce(s, query, limit);
    let note;
    if (r.total < 3 && !query.startsWith("|") && BOOK_PREFIX_RE.test(query)) {
      const shorter = query.replace(BOOK_PREFIX_RE, "");
      const r2 = await findBooksOnce(s, shorter, limit);
      if (r2.total > r.total) {
        note = `"${query}" found ${r.total} book(s); catalogue names usually leave out "${query.slice(0, query.length - shorter.length).trim()}", so these are the results for "${shorter}".`;
        used = shorter;
        r = r2;
      }
    }
    const out = { query, total_matches: r.total, returned: r.books.length, books: r.books };
    if (used !== query) out.searched_as = used;
    if (note) out.note = note;
    if (r.ref.trim()) out.reference_part = r.ref.trim();
    if (r.hidden) out.hidden_books_skipped = r.hidden;
    if (!r.total) out.hint = "No book matched. Try fewer words, another spelling, or the |ספר:..&מחבר:..| syntax.";
    return out;
  });
}

// ---------------------------------------------------------------------------------------------
// IT-781: the reader's index (tsiyunim): list_references, go_to_reference
// ---------------------------------------------------------------------------------------------
// Type 2 = the catalogue's fine reference (daf/amud, perek/halacha), type 1 = the book's own
// headings (siman, introduction...). Otzar itself tries type 2 first, then type 1.
const REF_TYPE_ORDER = [2, 1];

function normTitle(s) {
  return String(s)
    .replace(/[֑-ׇ]/g, (c) => (c === "־" ? " " : "")) // nikud and cantillation; maqaf -> space
    .replace(/["'״׳`]/g, "")
    .replace(/[\s ]+/g, " ")
    .trim();
}
// The forms of a reference to try: "דף יג." -> "דף יג א", "יג א"; "סימן קכ"ג" -> "סימן קכג".
function refVariants(ref) {
  let r = String(ref).trim();
  const amud = /^(.*\S)\s*([.:])$/.exec(r);
  if (amud) r = `${amud[1]} ${amud[2] === "." ? "א" : "ב"}`;
  r = normTitle(r.replace(/[.,:;()]/g, " ").replace(/\s+עמוד\s+/g, " "));
  const out = [r];
  const bare = r.replace(/^(?:דף|daf)\s+/i, "");
  if (bare && bare !== r) out.push(bare);
  return out.filter(Boolean);
}
const REF_TIERS = ["exact", "suffix", "whole_words", "substring"];
function tierOf(title, v) {
  if (title === v) return 0;
  if (title.endsWith(` ${v}`)) return 1;
  if (` ${title} `.includes(` ${v} `)) return 2;
  if (title.includes(v)) return 3;
  return -1;
}

// data = { refs: [[title, page, type]], pages: [[position, label]] } (non-empty pages).
// Returns the entries sorted the way the reader shows them: by type order, then page (stable).
function sortedRefs(data) {
  return data.refs.map(([title, page, type], i) => ({ title: String(title), norm: normTitle(title), page: Number(page), type: Number(type), i }))
    .filter((r) => Number.isInteger(r.page))
    .sort((a, b) => a.page - b.page || a.i - b.i);
}
// The otzar-mcp page for a tsiyun page: that position if it is a non-empty page, else the next
// non-empty one (else the last page).
function pageFor(data, page) {
  const ps = data.pages;
  const p = ps.find((x) => x[0] === page) || ps.find((x) => x[0] > page) || ps[ps.length - 1];
  return { page: p[0], page_label: p[1] || null };
}

function findReference(data, ref) {
  const refs = sortedRefs(data);
  const variants = refVariants(ref);
  if (!variants.length) return { found: false };
  const scored = [];
  for (const r of refs) {
    let best = -1;
    for (const v of variants) { const t = tierOf(r.norm, v); if (t >= 0 && (best < 0 || t < best)) best = t; }
    if (best >= 0) scored.push({ r, tier: best });
  }
  const rank = (t) => { const k = REF_TYPE_ORDER.indexOf(t); return k < 0 ? REF_TYPE_ORDER.length : k; };
  scored.sort((a, b) => a.tier - b.tier || rank(a.r.type) - rank(b.r.type) || a.r.page - b.r.page || a.r.i - b.r.i);
  if (scored.length) {
    const { r, tier } = scored[0];
    const sameType = refs.filter((x) => x.type === r.type);
    const k = sameType.indexOf(r);
    const nx = sameType.slice(k + 1).find((x) => x.norm !== r.norm);
    const seen = new Set([`${r.norm}|${r.page}`]);
    const candidates = [];
    for (const c of scored.slice(1)) {
      if (candidates.length >= 5) break;
      if (seen.has(`${c.r.norm}|${c.r.page}`)) continue;
      seen.add(`${c.r.norm}|${c.r.page}`);
      candidates.push({ title: c.r.title, type: c.r.type, ...pageFor(data, c.r.page), match: REF_TIERS[c.tier] });
    }
    return {
      found: true, matched_title: r.title, type: r.type, match: REF_TIERS[tier], ...pageFor(data, r.page),
      next_reference: nx ? { title: nx.title, ...pageFor(data, nx.page) } : null,
      candidates,
    };
  }
  // No index entry: a printed page/daf label ("יג", "יג א").
  for (const v of variants) {
    const p = data.pages.find((x) => normTitle(x[1]) === v) || data.pages.find((x) => normTitle(x[1]) === `${v} א`);
    if (p) return { found: true, matched_title: null, type: null, match: "page_label", page: p[0], page_label: p[1] || null, next_reference: null, candidates: [] };
  }
  return { found: false };
}

function listReferences(data, { filter, type, limit, offset }) {
  const refs = sortedRefs(data);
  const counts = { catalogue: refs.filter((r) => r.type === 2).length, headings: refs.filter((r) => r.type === 1).length };
  // Default: the catalogue references when the book has them (they are the fine ones), else all.
  const shown = type == null ? (counts.catalogue ? 2 : 0) : type;
  let list = shown ? refs.filter((r) => r.type === shown) : refs;
  if (shown === 0) list = list.slice().sort((a, b) => a.page - b.page || a.i - b.i);
  const f = filter ? normTitle(filter) : "";
  if (f) list = list.filter((r) => r.norm.includes(f));
  const page = list.slice(offset, offset + limit).map((r) => ({ title: r.title, ...(shown ? {} : { type: r.type }), ...pageFor(data, r.page) }));
  return {
    total_references: refs.length, type_counts: counts, type_shown: shown === 0 ? "all" : shown,
    filter: filter || undefined, total_matching: list.length, offset, returned: page.length,
    next_offset: offset + limit < list.length ? offset + limit : null, references: page,
  };
}

async function toolListReferences(args) {
  const book = int(args.book_id, "book_id", 1);
  const type = args.type == null ? null : int(args.type, "type", 0, 2);
  const limit = int(args.limit ?? 100, "limit", 1, 500);
  const offset = int(args.offset ?? 0, "offset", 0, 1e7);
  return withSession(async (s) => {
    const data = await bookData(s, book);
    const r = listReferences(data, { filter: args.filter ? String(args.filter).trim() : "", type, limit, offset });
    const out = { book_id: book, ...r };
    if (!r.total_references) out.note = "This book has no index (tsiyunim) in Otzar. Use page_label, or search_hits / get_hit_context on a scoped search.";
    return out;
  });
}

async function toolGoToReference(args) {
  const book = int(args.book_id, "book_id", 1);
  const ref = String(args.ref).trim();
  if (!ref) throw new UserError("ref is required");
  return withSession(async (s) => {
    const data = await bookData(s, book);
    const r = findReference(data, ref);
    if (!r.found) {
      return { book_id: book, ref, found: false, references_in_book: data.refs.length,
        hint: data.refs.length ? "No index entry matched. Call list_references with a filter (e.g. one word of the reference) to see the titles this book uses." : "This book has no index (tsiyunim) and no page with that label." };
    }
    const { found, ...rest } = r;
    return { book_id: book, ref, found: true, ...rest };
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
    description: "Full-text search in the user's Otzar HaChochma library, using Otzar's own search syntax (see the skill). With book_ids, only in those books (e.g. the volumes found with find_book). Returns the total number of results, the number of books with results, and the top books (book_id, name, volume, authors, year, results). The search runs in Otzar's search panel, so the user sees it there too, and Otzar adds it to its search history.",
    inputSchema: { type: "object", properties: {
      query: { type: "string", minLength: 1, maxLength: 300, singleLine: true, notBlank: true, description: "Search text in Otzar syntax, e.g. שבת or \"מעשה & בראשית\"." },
      book_ids: { type: "array", items: { type: "integer", minimum: 1, maximum: 999999999999 }, minItems: 1, maxItems: 200, description: "Search only in these books (1-200 ids). Omit to search the whole library." },
      limit: { type: "integer", minimum: 0, maximum: 200, description: "How many books to list (default 20)." },
      timeout_sec: { type: "integer", minimum: 10, maximum: 600, description: "Default 120." },
    }, required: ["query"], additionalProperties: false },
    run: async (a) => {
      const r = await doSearch(a.query, a.limit == null ? 20 : int(a.limit, "limit", 0, 200), a.timeout_sec == null ? 120 : int(a.timeout_sec, "timeout_sec", 10, 600), a.book_ids || null);
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
    name: "get_hit_context",
    description: "For one book from the current search results: each hit with only the words around it (window words before and after; hit words in «», a page change marked ‖). Much cheaper than reading whole pages. Pages through the hits with offset. Run search first.",
    inputSchema: { type: "object", properties: {
      book_id: BOOK_ID,
      window: { type: "integer", minimum: 1, maximum: 100, description: "Words before and after each hit (default 15)." },
      max_hits: { type: "integer", minimum: 1, maximum: 50, description: "Hits to return (default 10)." },
      offset: { type: "integer", minimum: 0, maximum: 10000000, description: "Skip this many hits (use next_offset)." },
    }, required: ["book_id"], additionalProperties: false },
    run: toolGetHitContext,
  },
  {
    name: "find_book",
    description: "Find books by name in the user's library (Otzar's F2 book search, in memory: no search panel change, no history). Returns book_id, name, volume, authors, place, year. For a multi-volume set, the main volume has volumes_in_set (the set size) and volume_ids (the book_id of every volume, for search book_ids); the other volumes have main_volume_id. Syntax: plain words; 'book name  reference' with TWO spaces returns reference_part for go_to_reference; |ספר:..&מחבר:..&מקום:..&שנה:..| searches by column.",
    inputSchema: { type: "object", properties: {
      query: { type: "string", minLength: 1, maxLength: 200, singleLine: true, notBlank: true, description: "Book name, e.g. אגרות משה, or משנה ברורה  סימן א, or |ספר:משנה ברורה&מחבר:כהן|." },
      limit: { type: "integer", minimum: 1, maximum: 200, description: "How many books to list (default 20)." },
    }, required: ["query"], additionalProperties: false },
    run: toolFindBook,
  },
  {
    name: "list_references",
    description: "The book's index (ציונים) as Otzar's reader shows it: titles such as siman, daf/amud, perek/halacha, with the page each starts on. Type 2 = catalogue references (daf, halacha), type 1 = the book's own headings (siman, introduction). Default: type 2 if the book has it, else all. Use filter and limit; big books have thousands.",
    inputSchema: { type: "object", properties: {
      book_id: BOOK_ID,
      filter: { type: "string", minLength: 1, maxLength: 100, singleLine: true, notBlank: true, description: "Only titles containing this text, e.g. סימן קכ." },
      type: { type: "integer", minimum: 0, maximum: 2, description: "2 = catalogue references, 1 = the book's headings, 0 = all." },
      limit: { type: "integer", minimum: 1, maximum: 500, description: "Default 100." },
      offset: { type: "integer", minimum: 0, maximum: 10000000, description: "Use next_offset." },
    }, required: ["book_id"], additionalProperties: false },
    run: toolListReferences,
  },
  {
    name: "go_to_reference",
    description: "The page where a reference starts in a book (siman, daf, halacha, chapter), from Otzar's own index, even when the scan does not show the number. Returns page and page_label for get_page_text / get_page_image, the matched index title, how it matched, and next_reference (where the next one starts). Nothing changes in Otzar's window.",
    inputSchema: { type: "object", properties: {
      book_id: BOOK_ID,
      ref: { type: "string", minLength: 1, maxLength: 100, singleLine: true, notBlank: true, description: "e.g. סימן קכג, הלכות תשובה ג א, ב א, דף יג., or reference_part from find_book." },
    }, required: ["book_id", "ref"], additionalProperties: false },
    run: toolGoToReference,
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
    } else if (p.type === "array") {
      // Only arrays of integers are used (book_ids). Duplicates are dropped, order is kept.
      const min = p.minItems ?? 0, max = p.maxItems ?? 1000;
      if (!Array.isArray(v) || v.length < min || v.length > max) throw new UserError(`${k} must be a list of ${min} to ${max} integers`);
      const imin = p.items?.minimum ?? 0, imax = p.items?.maximum ?? 1e12;
      const list = v.map((x) => {
        const n = typeof x === "string" && /^[0-9]{1,15}$/.test(x) ? Number(x) : x;
        if (typeof n !== "number" || !Number.isInteger(n) || n < imin || n > imax) throw new UserError(`${k} must contain only integers between ${imin} and ${imax}`);
        return n;
      });
      out[k] = [...new Set(list)];
    } else {
      throw new Error(`internal: unsupported schema type for ${k}`);
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
            "To check a citation: find_book -> go_to_reference -> get_page_text; to judge hits cheaply use get_hit_context, and search with book_ids to stay inside one book. " +
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
  if (m === MSG_STILL_LOADING || /is still running\. Wait a minute/.test(m)) return "אוצר החכמה עדיין נפתח.\nחכו שיסיים להיטען. אין צורך לפתוח אותו שוב.";
  if (/did not answer within/.test(m)) return "אוצר החכמה נפתח, אבל החיבור ל-AI עדיין לא עונה.\nאם אוצר החכמה עדיין נטען, חכו לו. אם מופיעה בו הודעת שגיאה, סגרו אותו ופתחו שוב מקיצור הדרך הזה.";
  if (/installation not found/.test(m)) return "לא נמצאה התקנה של אוצר החכמה במחשב הזה.";
  // IT-788
  const code = e instanceof UserError ? e.code : null;
  if (code === "no_disk") return "הדיסק של אוצר החכמה לא מחובר למחשב, ולכן אוצר החכמה לא נפתח.\nחברו את הדיסק של אוצר החכמה ונסו שוב.";
  if (code === "not_answering" || code === "not_answering_no_disk") {
    return (code === "not_answering_no_disk" ? "הדיסק של אוצר החכמה לא מחובר למחשב.\n" : "") +
      "אוצר החכמה נפתח, אבל הוא לא מגיב.\nבדקו שהדיסק של אוצר החכמה מחובר. אם מופיעה באוצר החכמה הודעה, סגרו אותו.\n" +
      "אם אי אפשר לסגור את אוצר החכמה, הפעילו מחדש את המחשב, ואז פתחו אותו שוב מקיצור הדרך הזה.";
  }
  return "לא הצלחנו לפתוח את אוצר החכמה עם החיבור ל-AI.\nנסו שוב בעוד דקה. אם זה חוזר, פתחו את אוצר החכמה מקיצור הדרך הרגיל.";
}

// What the shortcut runs. Returns the exit code. Errors go to a message box, since the window
// is minimized.
async function launchFromShortcut() {
  try {
    // An Otzar that another double-click (or an agent) just opened is still loading: wait for it
    // and bring it to the front, instead of telling the user to close it (IT-779 review N2).
    await ensureOtzar({ waitForWindow: false, loadingWaitMs: LAUNCH_WAIT_MS });
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
      const bi = process.argv.indexOf("--book");
      const r = cmd === "launch"
        ? { cdp: `${CDP_HOST}:${await ensureOtzar()}`, started_by_this_tool: state.launchedByUs }
        : await toolStatus({ coverage_check: process.argv.includes("--coverage") });
      if (cmd === "doctor") r.disk = await deps.diskState(state.install || { exe: r.install });
      if (cmd === "doctor" && bi > 0) r.index_check = await indexCheck(int(process.argv[bi + 1], "--book", 1));
      if (state.warnings.size) r.warnings = [...state.warnings];
      process.stdout.write(JSON.stringify(r, null, 1) + "\n");
      process.exit(0);
    } catch (e) {
      process.stderr.write(e.message + "\n");
      process.exit(e instanceof UserError ? 2 : 1);
    }
  }
  process.stderr.write("usage: otzar-mcp [serve | launch | doctor [--coverage] [--book <id>] | shortcut]\n");
  process.exit(64);
}

if (isSea || require.main === module) cli();
module.exports = { validateArgs, runSearch, checkedPath, ROUTES_GET, ROUTE_POST_OCR, ROUTE_GET_DATA, findInstall,
  findReference, listReferences, refVariants, normTitle, groupOffsets, pageSegments, planHitWindow, hitText, cmdPorts,
  shortcutPopupText, MSG_STILL_LOADING, MSG_NO_DISK, STUCK_AFTER_MS, notAnsweringError, otzarDiskState, otzarLocalDir, DISK_MARKERS, DISK_PROBE_MS, scanDrives, probeDrive, BOOK_DATA_PAGE_FN, bookDataError, POPUP_TIMEOUT_MS, BOOK_PREFIX_RE, startLockName, tryLock, otzarProcesses, cdpVersion, ensureOtzar, deps, state, TOOLS, callTool, errorText, makeShortcut, launchFromShortcut, SHORTCUT_NAME, SHORTCUT_ARGS, START_CAP, START_WINDOW_MS, MSG_ALREADY_OPEN, UserError };
