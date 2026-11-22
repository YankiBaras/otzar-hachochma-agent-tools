// IT-742 offline tests. They never touch a real Otzar: process discovery, install discovery and
// spawn are replaced through the `deps` seam, and every CDP endpoint is a fake HTTP server.
// Run: node --test package/test/offline.test.cjs
"use strict";
process.env.OTZAR_MCP_LAUNCH_WAIT_SEC = "5";
delete process.env.OTZAR_CDP_PORT;
delete process.env.OTZAR_MCP_NO_LAUNCH;

const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const path = require("node:path");
const m = require("../src/otzar-mcp.cjs");

const GET = m.ROUTES_GET;
const POST = [m.ROUTE_POST_OCR];
// A start lock of its own, so the tests never wait on (or block) a real otzar-mcp on this PC.
const TEST_LOCK = `\\\\.\\pipe\\otzar-mcp-test-${process.pid}`;
m.deps.startLockName = () => TEST_LOCK;

function fakeCdp(ua, port = 0) {
  return new Promise((ok) => {
    const srv = http.createServer((req, res) => {
      res.setHeader("Content-Type", "application/json");
      if (req.url === "/json/version") return res.end(JSON.stringify({ "User-Agent": ua }));
      if (req.url === "/json/list") return res.end("[]");
      res.statusCode = 404; res.end("{}");
    });
    srv.listen(port, "127.0.0.1", () => ok(srv));
  });
}
// IT-788: the data-disk check is replaced too (default: the disk is connected), so no test reads
// this PC's drives.
const DISK_PRESENT = () => ({ state: "present", drives: ["T:"] });
m.deps.diskState = DISK_PRESENT;
const reset = () => {
  Object.assign(m.state, { port: null, install: null, launchedByUs: false, starts: [], noAnswer: false, lastStartPort: null });
  m.deps.diskState = DISK_PRESENT;
};

test("route guard: allowed routes", () => {
  for (const p of ["api/books/182057", "api/books/182057/pages", "api/books/pageData/182057/1",
    "api/books/pageName/182057/1", "api/freesearch/words/search/46hH7O1HzdTOT/0/159/2"]) {
    assert.equal(m.checkedPath(p, GET), p);
  }
  assert.equal(m.checkedPath("api/books/1/pages/2/ocr", POST), "api/books/1/pages/2/ocr");
});

test("route guard: refused paths", () => {
  const bad = [
    "api/books/1/../license", "api/books/..", "api/books/./1", "api/books//1", "api/books/C:",
    "api/books/1:x", "api/books/%2e%2e", "api/books/1?x=1", "api/books/1#x", "api/books/1\\x",
    "api/license", "api/user/factory-reset", "api/general/shutdown-app", "api/general/o-ver",
    "api/books/abc", "api/books/1/pages/2/ocr", "api/books/pageData/1", "data/1", "txt/1/1/1",
    "api/freesearch/words/search/a-b/1/2/3", "api/books/\u05d0", "api/books/1 ",
  ];
  for (const p of bad) assert.throws(() => m.checkedPath(p, GET), /internal:/, p);
  for (const p of ["api/books/1/pages/x/ocr", "api/books/1/pages/2/ocr/x", "api/books/1"]) {
    assert.throws(() => m.checkedPath(p, POST), /internal:/, p);
  }
});

test("cdpVersion accepts only an Otzar browser", async () => {
  const chrome = await fakeCdp("Mozilla/5.0 Chrome/120.0 Safari/537.36");
  const otzar = await fakeCdp("Mozilla/5.0 otzar/1.0.0 Chrome/100.0 Electron/18.2.0 Safari/537.36");
  try {
    assert.equal(await m.cdpVersion(chrome.address().port), null);
    assert.ok(await m.cdpVersion(otzar.address().port));
  } finally { chrome.close(); otzar.close(); }
});

test("Otzar running without the debugging port: clear message, nothing killed or started", async () => {
  reset();
  let spawned = false;
  m.deps.otzarProcesses = () => [{ pid: 1, exe: "X:\\otzar.exe", cmd: "\"X:\\otzar.exe\"" }];
  m.deps.spawn = () => { spawned = true; return { unref() {} }; };
  await assert.rejects(m.ensureOtzar(), (e) => e instanceof m.UserError && e.message === m.MSG_ALREADY_OPEN && /otzar-mcp\.exe shortcut/.test(e.message));
  assert.equal(spawned, false);
});

test("a port on the command line that is not Otzar is never used", async () => {
  reset();
  const chrome = await fakeCdp("Mozilla/5.0 Chrome/120.0");
  try {
    m.deps.otzarProcesses = () => [{ pid: 1, exe: "X:\\otzar.exe", cmd: `otzar.exe --remote-debugging-port=${chrome.address().port}` }];
    // IT-781 (N2): a port on the command line that does not answer as Otzar is treated as an
    // Otzar still loading (bounded wait), and is still never used.
    await assert.rejects(m.ensureOtzar({ loadingWaitMs: 1500 }), (e) => e.message === m.MSG_STILL_LOADING);
    assert.equal(m.state.port, null);
  } finally { chrome.close(); }
});

test("automatic start: free loopback port, one switch only, app folder as cwd", async () => {
  reset();
  let call = null;
  let srv = null;
  m.deps.otzarProcesses = () => [];
  m.deps.findInstall = () => ({ exe: "Z:\\Otzar\\launcher\\bin\\x86\\app\\otzar.exe", source: "test" });
  m.deps.spawn = (exe, args, opts) => {
    call = { exe, args, opts };
    const port = Number(/--remote-debugging-port=(\d+)/.exec(args[0])[1]);
    fakeCdp("Mozilla/5.0 otzar/1.0.0 Electron/18.2.0", port).then((s) => { srv = s; });
    return { unref() {} };
  };
  try {
    const port = await m.ensureOtzar();
    assert.ok(port > 0);
    assert.deepEqual(call.args, [`--remote-debugging-port=${port}`]);
    assert.equal(call.opts.cwd, path.dirname("Z:\\Otzar\\launcher\\bin\\x86\\app\\otzar.exe"));
    assert.equal(m.state.launchedByUs, true);
    assert.equal(srv.address().address, "127.0.0.1");
  } finally { if (srv) srv.close(); }
});

test("automatic start can be disabled", async () => {
  reset();
  process.env.OTZAR_MCP_NO_LAUNCH = "1";
  m.deps.otzarProcesses = () => [];
  try {
    await assert.rejects(m.ensureOtzar(), /automatic start is disabled/);
  } finally { delete process.env.OTZAR_MCP_NO_LAUNCH; }
});

test("no install found: clear message", async () => {
  reset();
  m.deps.otzarProcesses = () => [];
  m.deps.findInstall = () => null;
  await assert.rejects(m.ensureOtzar(), /installation not found/);
});

test("invalid arguments are refused before any Otzar call, even when Otzar could be started", async () => {
  reset();
  // Otzar not running, install found: a call that reached ensureOtzar would spawn.
  let spawned = 0;
  let probed = 0;
  m.deps.otzarProcesses = () => { probed++; return []; };
  m.deps.findInstall = () => ({ exe: "Z:\\Otzar\\launcher\\bin\\x64\\app\\otzar.exe", source: "test" });
  m.deps.spawn = () => { spawned++; return { unref() {} }; };
  const cases = [
    ["book_info", { book_id: "1; alert(1)" }, /^book_id must be an integer/],
    ["book_info", { book_id: 0 }, /^book_id must be an integer/],
    ["book_info", { book_id: 1e12 }, /^book_id must be an integer/],
    ["book_info", {}, /^book_id is required/],
    ["book_info", { book_id: 1, extra: 1 }, /^unknown argument: extra/],
    ["book_info", "x", /^arguments must be an object/],
    ["search", { query: "x", limit: 999 }, /^limit must be an integer/],
    ["search", { query: "   " }, /^query is required/],
    ["search", { query: "a\nb" }, /^query must be one line/],
    ["search", { query: "x".repeat(301) }, /^query must be 1 to 300 characters/],
    ["search", { query: 5 }, /^query must be a string/],
    ["search", { query: "x", timeout_sec: 1 }, /^timeout_sec must be an integer/],
    ["search_hits", { book_id: 1, snippets: "yes" }, /^snippets must be true or false/],
    ["get_page_text", { book_id: -5 }, /^book_id must be an integer/],
    ["get_page_text", { book_id: 1, page: 0 }, /^page must be an integer/],
    ["get_page_text", { book_id: 1, page_label: "a\u0000" }, /^page_label must be one line/],
    ["get_page_image", { book_id: 1, width: 5000 }, /^width must be an integer/],
    ["status", { coverage_check: 1 }, /^coverage_check must be true or false/],
    ["nosuch", {}, /^Unknown tool/],
  ];
  for (const [name, args, re] of cases) {
    const r = await m.callTool(name, args);
    assert.equal(r.isError, true, `${name} ${JSON.stringify(args)}`);
    assert.match(r.content[0].text, re, `${name} ${JSON.stringify(args)}`);
    assert.doesNotMatch(r.content[0].text, /installation not found|did not answer|started/);
  }
  assert.equal(spawned, 0, "no Otzar was started");
  assert.equal(probed, 0, "the process list was not even read");
  assert.equal(m.state.launchedByUs, false);
});

test("validateArgs passes clean arguments through (digit strings become integers)", () => {
  const schema = m.TOOLS.find((t) => t.name === "get_page_text").inputSchema;
  assert.deepEqual(m.validateArgs(schema, { book_id: "182057", page: 2 }), { book_id: 182057, page: 2 });
  assert.deepEqual(m.validateArgs(schema, { book_id: 1, page_label: "\u05d9\u05d2" }), { book_id: 1, page_label: "\u05d9\u05d2" });
});

// ---- runSearch: never return a search other than the one submitted (IT-742 review r1) ----
// A fake Otzar search store driven by a script of states, with fake time.
function fakeIo(script, { submitAt = null } = {}) {
  let t = 0, i = 0, submitted = false, submittedAtRead = null;
  const io = {
    read: async () => {
      const st = typeof script === "function" ? script(i, submitted) : script[Math.min(i, script.length - 1)];
      i++;
      return st;
    },
    submit: async () => { submitted = true; submittedAtRead = i; },
    wait: async (ms) => { t += ms; },
    now: () => t,
  };
  return { io, info: () => ({ submitted, submittedAtRead, reads: i }) };
}
const OLD = { hid: 7, searching: false, text: "\u05e9\u05d1\u05ea" }; // previous search: shabbat
const Q = "\u05d1\u05e8\u05d0\u05e9\u05d9\u05ea"; // bereshit

test("runSearch: Otzar already searching -> waits for idle before submitting, ignores the old search", async () => {
  // reads 0-2: old search still running; 3: old search done (old hid, old text);
  // after submit: 3 reads of the gap (idle, old hid, old text), then ours runs, then ours done.
  let afterSubmit = 0;
  const { io, info } = fakeIo((i, submitted) => {
    if (!submitted) return i < 3 ? { ...OLD, searching: true } : OLD;
    afterSubmit++;
    if (afterSubmit <= 3) return OLD; // the gap: idle, old history id, old text
    if (afterSubmit <= 5) return { hid: 8, searching: true, text: Q };
    return { hid: 8, searching: false, text: Q };
  });
  const st = await m.runSearch(io, Q, 60_000);
  assert.equal(st.text, Q);
  assert.equal(st.hid, 8);
  assert.equal(info().submittedAtRead, 4, "submitted only after Otzar was idle");
});

test("runSearch: the gap with the old history id and old text never ends the wait", async () => {
  // Otzar was mid-search when the tool was called; the old search ends; ours never starts.
  const { io } = fakeIo((i, submitted) => (!submitted && i < 2 ? { ...OLD, searching: true } : OLD));
  await assert.rejects(m.runSearch(io, Q, 60_000), (e) => e instanceof m.UserError && /did not start/.test(e.message));
});

test("runSearch: a search that started but ends with another text is an error, not a result", async () => {
  let n = 0;
  const { io } = fakeIo((i, submitted) => {
    if (!submitted) return OLD;
    n++;
    return n < 3 ? { hid: 8, searching: true, text: Q } : { hid: 9, searching: false, text: "\u05d0\u05d7\u05e8" };
  });
  await assert.rejects(m.runSearch(io, Q, 20_000), /Another search replaced this one/);
});

test("runSearch: the same query again (same text as before) still needs a new cycle", async () => {
  const same = { hid: 7, searching: false, text: Q };
  let n = 0;
  const { io } = fakeIo((i, submitted) => {
    if (!submitted) return same;
    n++;
    if (n <= 2) return same; // not started yet: must not return the previous identical result
    if (n <= 4) return { hid: 8, searching: true, text: Q };
    return { hid: 8, searching: false, text: Q };
  });
  const st = await m.runSearch(io, Q, 60_000);
  assert.equal(st.hid, 8);
  assert.ok(n >= 5);
});

test("runSearch: busy with another search past the timeout -> error, nothing submitted", async () => {
  const { io, info } = fakeIo(() => ({ ...OLD, searching: true }));
  await assert.rejects(m.runSearch(io, Q, 5_000), /busy with another search/);
  assert.equal(info().submitted, false);
});

test("runSearch: results are returned only after they repeat unchanged (settled list)", async () => {
  let n = 0;
  const done = (total) => ({ hid: 8, searching: false, text: Q, results: { query: Q, total_results: total, books_with_results: 2 } });
  const { io } = fakeIo((i, submitted) => {
    if (!submitted) return OLD;
    n++;
    if (n <= 2) return { hid: 8, searching: true, text: Q };
    if (n === 3) return done(10); // first idle read: list still filling
    return done(42);              // settled
  });
  const st = await m.runSearch(io, Q, 60_000);
  assert.equal(st.results.total_results, 42);
});

test("runSearch: another search right after ours finished returns our snapshot, never theirs", async () => {
  let n = 0;
  const { io } = fakeIo((i, submitted) => {
    if (!submitted) return OLD;
    n++;
    if (n <= 2) return { hid: 8, searching: true, text: Q };
    if (n === 3) return { hid: 8, searching: false, text: Q, results: { query: Q, total_results: 42, books_with_results: 2 } };
    return { hid: 9, searching: true, text: "אחר" }; // someone else's search took over
  });
  const st = await m.runSearch(io, Q, 60_000);
  assert.equal(st.text, Q);
  assert.equal(st.results.query, Q);
  assert.equal(st.results.total_results, 42);
});

test("runSearch: replaced by a search whose text equals the previous one -> fast error, not a timeout", async () => {
  // Two clients submitted at once; the other one's query (same as the text before) won.
  const prev = { hid: 7, searching: false, text: "אחר" };
  let n = 0;
  const { io, info } = fakeIo((i, submitted) => {
    if (!submitted) return prev;
    n++;
    if (n <= 2) return { hid: 7, searching: true, text: prev.text };
    return { hid: 8, searching: false, text: prev.text, results: { query: prev.text, total_results: 5 } };
  });
  await assert.rejects(m.runSearch(io, Q, 120_000), /Another search replaced this one/);
  assert.ok(info().reads < 10, "failed fast");
});

// ---- IT-779: reopen after the user closed Otzar, with launch-loop protection ----
const UA_OTZAR = "Mozilla/5.0 otzar/1.0.0 Chrome/100.0 Electron/18.2.0 Safari/537.36";
const INSTALL = "Z:\\Otzar\\launcher\\bin\\x64\\app\\otzar.exe";
// A fake PC: spawn "starts Otzar" (a fake CDP on the requested port, unless answers=false),
// close() is the user closing Otzar. Time for the start cap is fake (m.deps.now).
function fakeMachine({ answers = true } = {}) {
  const mc = { spawns: 0, srv: null, running: false, port: null, extra: null, now: 1_000_000 };
  m.deps.now = () => mc.now;
  m.deps.findInstall = () => ({ exe: INSTALL, source: "test" });
  m.deps.otzarProcesses = () => [
    ...(mc.running ? [{ pid: 4242, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${mc.port}` }] : []),
    ...(mc.extra ? [mc.extra] : []),
  ];
  m.deps.spawn = (exe, args) => {
    mc.spawns++;
    mc.port = Number(/^--remote-debugging-port=(\d+)$/.exec(args[0])[1]);
    mc.running = true;
    if (answers) fakeCdp(UA_OTZAR, mc.port).then((s) => { mc.srv = s; });
    return { unref() {} };
  };
  mc.close = () => new Promise((ok) => {
    mc.running = false;
    const s = mc.srv; mc.srv = null;
    if (!s) return ok();
    s.close(() => ok()); s.closeAllConnections();
  });
  return mc;
}
const closeSrv = (s) => new Promise((ok) => { s.close(() => ok()); s.closeAllConnections(); });

test("IT-779: Otzar closed by the user -> the next call reopens it, no restart of the server needed", async () => {
  reset();
  const mc = fakeMachine();
  try {
    const p1 = await m.ensureOtzar({ waitForWindow: false });
    assert.equal(mc.spawns, 1);
    assert.equal(await m.ensureOtzar({ waitForWindow: false }), p1, "connected Otzar is reused, not started again");
    assert.equal(mc.spawns, 1);
    await mc.close(); // the user closes Otzar
    mc.now += 60_000;
    const p2 = await m.ensureOtzar({ waitForWindow: false });
    assert.equal(mc.spawns, 2, "started again");
    assert.equal(m.state.port, p2);
    assert.equal(m.state.noAnswer, false);
  } finally { await mc.close(); }
});

test("IT-779: start cap -> a third automatic start within 10 minutes is refused, allowed again after the window", async () => {
  reset();
  const mc = fakeMachine();
  try {
    for (let i = 0; i < m.START_CAP; i++) {
      await m.ensureOtzar({ waitForWindow: false });
      await mc.close();
      mc.now += 60_000;
    }
    assert.equal(mc.spawns, 2);
    await assert.rejects(m.ensureOtzar({ waitForWindow: false }),
      (e) => e instanceof m.UserError && /2 times in the last 10 minutes/.test(e.message) && /Tell the user/.test(e.message) && /in 8 minute/.test(e.message));
    assert.equal(mc.spawns, 2, "nothing started");
    mc.now += m.START_WINDOW_MS; // the window has passed
    await m.ensureOtzar({ waitForWindow: false });
    assert.equal(mc.spawns, 3);
  } finally { await mc.close(); }
});

test("IT-779: a start that never answered -> no further automatic start until a connection succeeds", async () => {
  reset();
  const mc = fakeMachine({ answers: false });
  try {
    await assert.rejects(m.ensureOtzar({ waitForWindow: false }), (e) => e instanceof m.UserError && /did not answer within 5s/.test(e.message));
    assert.equal(mc.spawns, 1);
    assert.equal(m.state.noAnswer, true);
    // The broken instance is still there: a clear message, nothing started.
    await assert.rejects(m.ensureOtzar({ waitForWindow: false }), /started by this tool, is running but its agent connection does not answer/);
    // The broken instance went away (closed by the user, or crashed): still no new start.
    mc.running = false;
    mc.now += 60 * 60_000; // even long after: only a connection clears it
    await assert.rejects(m.ensureOtzar({ waitForWindow: false }),
      (e) => e instanceof m.UserError && /never answered/.test(e.message) && /otzar-mcp\.exe shortcut/.test(e.message));
    assert.equal(mc.spawns, 1, "no loop");
    // The user opens Otzar from the shortcut (some other port): the tool connects and is unblocked.
    const user = await fakeCdp(UA_OTZAR);
    mc.extra = { pid: 77, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${user.address().port}` };
    assert.equal(await m.ensureOtzar({ waitForWindow: false }), user.address().port);
    assert.equal(m.state.noAnswer, false);
    // The user closes it later: the next call may start Otzar again.
    mc.extra = null;
    await closeSrv(user);
    await assert.rejects(m.ensureOtzar({ waitForWindow: false }), /did not answer/); // (this fake never answers)
    assert.equal(mc.spawns, 2);
  } finally { mc.running = false; }
});

test("IT-779: OTZAR_MCP_NO_LAUNCH and Otzar-open-without-port messages point to the shortcut", async () => {
  reset();
  const mc = fakeMachine();
  process.env.OTZAR_MCP_NO_LAUNCH = "1";
  try {
    await assert.rejects(m.ensureOtzar(), (e) => /automatic start is disabled/.test(e.message) && /otzar-mcp\.exe shortcut/.test(e.message));
  } finally { delete process.env.OTZAR_MCP_NO_LAUNCH; }
  mc.extra = { pid: 5, exe: INSTALL, cmd: `"${INSTALL}"` };
  await assert.rejects(m.ensureOtzar(), (e) => e.message === m.MSG_ALREADY_OPEN && /Do not retry/.test(e.message));
  assert.equal(mc.spawns, 0);
});

test("IT-779: Otzar closed during a call -> a plain message for the agent, not a socket error", async () => {
  reset();
  const gone = await fakeCdp(UA_OTZAR);
  const port = gone.address().port;
  await closeSrv(gone);
  m.state.port = port;
  const t = await m.errorText(new Error("CDP connection closed"));
  assert.match(t, /^Otzar HaChochma was closed, or stopped answering, during this call/);
  assert.equal(m.state.port, null);
  assert.equal(await m.errorText(new m.UserError("x")), "x");
  const live = await fakeCdp(UA_OTZAR);
  try {
    m.state.port = live.address().port;
    assert.equal(await m.errorText(new Error("boom")), "Error: boom");
  } finally { await closeSrv(live); m.state.port = null; }
});

// ---- IT-779: the desktop shortcut command (Windows shell calls mocked) ----
const os = require("node:os");
const SELF = "C:\\Users\\u\\AppData\\Local\\OtzarMcp\\otzar-mcp.exe";
function fakeShell({ existing = null } = {}) {
  const sh = { writes: [], popups: [], activated: [] };
  m.deps.selfExe = () => SELF;
  m.deps.findInstall = () => ({ exe: INSTALL, source: "test" });
  m.deps.desktopDir = () => os.tmpdir(); // only checked for existence; nothing is written there
  m.deps.readShortcut = () => existing;
  m.deps.writeShortcut = (spec) => sh.writes.push(spec);
  m.deps.popup = (t) => sh.popups.push(t);
  m.deps.activate = (pids) => sh.activated.push(pids);
  return sh;
}

test("IT-779 shortcut: one .lnk on the desktop that runs this tool's launch, Otzar's icon, nothing else", async () => {
  const sh = fakeShell();
  const r = await m.makeShortcut();
  assert.equal(sh.writes.length, 1);
  const s = sh.writes[0];
  assert.equal(s.path, path.join(os.tmpdir(), "\u05d0\u05d5\u05e6\u05e8 \u05d4\u05d7\u05db\u05de\u05d4 \u05dc-AI.lnk"));
  assert.equal(s.target, SELF);
  assert.equal(s.args, "launch --shortcut");
  assert.equal(s.workingDir, path.dirname(SELF));
  assert.equal(s.icon, `${INSTALL},0`);
  assert.ok(!s.path.toLowerCase().startsWith(path.dirname(INSTALL).toLowerCase()), "not inside the Otzar folder");
  assert.doesNotMatch(s.target, /otzar\.exe$/i, "never a shortcut straight to otzar.exe");
  assert.equal(r.replaced_previous, false);
});

test("IT-779 shortcut: an existing shortcut of this tool is updated; any other file of that name is left alone", async () => {
  let sh = fakeShell({ existing: { target: "D:\\old\\otzar-mcp.exe", args: "launch --shortcut" } });
  assert.equal((await m.makeShortcut()).replaced_previous, true);
  assert.equal(sh.writes.length, 1);
  for (const target of [INSTALL, "", "C:\\x\\other.exe", "C:\\x\\otzar-mcp.exe.bat"]) {
    sh = fakeShell({ existing: { target, args: "" } });
    await assert.rejects(m.makeShortcut(), (e) => e instanceof m.UserError && /left unchanged/.test(e.message), target);
    assert.equal(sh.writes.length, 0, target);
  }
});

test("IT-779 shortcut: refused when not run from the exe, or when Otzar is not installed", async () => {
  let sh = fakeShell();
  m.deps.selfExe = () => null;
  await assert.rejects(m.makeShortcut(), /from otzar-mcp\.exe/);
  sh = fakeShell();
  m.deps.findInstall = () => null;
  await assert.rejects(m.makeShortcut(), /installation not found/);
  assert.equal(sh.writes.length, 0);
});

test("IT-779 launch --shortcut: Otzar not running -> started once with a free port, no window wait, no popup", async () => {
  reset();
  const sh = fakeShell();
  const mc = fakeMachine();
  try {
    assert.equal(await m.launchFromShortcut(), 0);
    assert.equal(mc.spawns, 1);
    assert.equal(sh.popups.length, 0);
    assert.equal(sh.activated.length, 0);
  } finally { await mc.close(); }
});

test("IT-779 launch --shortcut: already running with the connection -> only brought to the front", async () => {
  reset();
  const sh = fakeShell();
  const mc = fakeMachine();
  const user = await fakeCdp(UA_OTZAR);
  try {
    mc.extra = { pid: 77, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${user.address().port}` };
    assert.equal(await m.launchFromShortcut(), 0);
    assert.equal(mc.spawns, 0);
    assert.deepEqual(sh.activated, [[77]]);
    assert.equal(sh.popups.length, 0);
  } finally { await closeSrv(user); }
});

test("IT-779 launch --shortcut: open without the connection -> Hebrew popup, nothing closed or started", async () => {
  reset();
  const sh = fakeShell();
  const mc = fakeMachine();
  mc.extra = { pid: 5, exe: INSTALL, cmd: `"${INSTALL}"` };
  assert.equal(await m.launchFromShortcut(), 2);
  assert.equal(mc.spawns, 0);
  assert.equal(sh.popups.length, 1);
  assert.match(sh.popups[0], /^\u05d0\u05d5\u05e6\u05e8 \u05d4\u05d7\u05db\u05de\u05d4 \u05db\u05d1\u05e8 \u05e4\u05ea\u05d5\u05d7/); // "Otzar HaChochma is already open"
  assert.doesNotMatch(sh.popups[0], /[A-Za-z]{3,}/, "Hebrew only: no agent text in the user's dialog");
});

test("IT-779 launch --shortcut: other failures -> a Hebrew-only popup for a person, never the agent text", async () => {
  reset();
  const sh = fakeShell();
  const mc = fakeMachine();
  m.deps.findInstall = () => null; // install not found
  assert.equal(await m.launchFromShortcut(), 2);
  m.deps.otzarProcesses = () => { throw new Error("boom"); }; // unexpected error
  assert.equal(await m.launchFromShortcut(), 1);
  assert.equal(mc.spawns, 0);
  assert.equal(sh.popups.length, 2);
  assert.match(sh.popups[0], /לא נמצאה התקנה/); // "no install was found"
  for (const p of sh.popups) assert.doesNotMatch(p, /[A-Za-z]{3,}|Tell the user|retry/);
});

// =============================================================================================
// IT-781: find_book, scoped search, hit context, references, and IT-779 review notes N1-N3
// =============================================================================================
const net = require("node:net");

test("IT-781 route guard: api/books/data/<id> only on its own binary route, never through the JSON GET list", () => {
  const DATA = [m.ROUTE_GET_DATA];
  assert.equal(m.checkedPath("api/books/data/173917", DATA), "api/books/data/173917");
  assert.throws(() => m.checkedPath("api/books/data/173917", GET), /internal:/);
  for (const p of ["api/books/data/x", "api/books/data/1/2", "api/books/data/../1", "api/books/data/1?x=1",
    "api/books/173917/tsiyunim", "api/books/data/1234567890123", "api/books/books_tsiyunim_fix", "api/books/tsiyunsDel"]) {
    assert.throws(() => m.checkedPath(p, DATA), /internal:/, p);
  }
  // Nothing new was added to the JSON GET list or the POST route.
  assert.equal(m.ROUTES_GET.length, 5);
  for (const p of ["api/books/1/tsiyunim", "api/books/1/pages/2/tsiyunim", "api/books/data/1"]) assert.throws(() => m.checkedPath(p, GET), /internal:/, p);
});

test("IT-781: invalid arguments of the new tools are refused before any Otzar call", async () => {
  reset();
  let probed = 0, spawned = 0;
  m.deps.otzarProcesses = () => { probed++; return []; };
  m.deps.findInstall = () => ({ exe: "Z:\\Otzar\\launcher\\bin\\x64\\app\\otzar.exe", source: "test" });
  m.deps.spawn = () => { spawned++; return { unref() {} }; };
  const cases = [
    ["search", { query: "x", book_ids: [] }, /^book_ids must be a list of 1 to 200/],
    ["search", { query: "x", book_ids: "1,2" }, /^book_ids must be a list/],
    ["search", { query: "x", book_ids: [1, "2; x"] }, /^book_ids must contain only integers/],
    ["search", { query: "x", book_ids: [0] }, /^book_ids must contain only integers/],
    ["search", { query: "x", book_ids: [1.5] }, /^book_ids must contain only integers/],
    ["search", { query: "x", book_ids: Array.from({ length: 201 }, (_, i) => i + 1) }, /^book_ids must be a list of 1 to 200/],
    ["find_book", {}, /^query is required/],
    ["find_book", { query: "  " }, /^query is required/],
    ["find_book", { query: "a\nb" }, /^query must be one line/],
    ["find_book", { query: "x".repeat(201) }, /^query must be 1 to 200/],
    ["find_book", { query: "x", limit: 0 }, /^limit must be an integer/],
    ["get_hit_context", { book_id: 1, window: 101 }, /^window must be an integer/],
    ["get_hit_context", { book_id: 1, max_hits: 51 }, /^max_hits must be an integer/],
    ["get_hit_context", { book_id: 1, offset: -1 }, /^offset must be an integer/],
    ["list_references", { book_id: 1, type: 3 }, /^type must be an integer/],
    ["list_references", { book_id: 1, limit: 501 }, /^limit must be an integer/],
    ["list_references", { book_id: 1, filter: "a\u0007" }, /^filter must be one line/],
    ["go_to_reference", { book_id: 1 }, /^ref is required/],
    ["go_to_reference", { book_id: 1, ref: " " }, /^ref is required/],
    ["go_to_reference", { book_id: "1 OR 1", ref: "x" }, /^book_id must be an integer/],
  ];
  for (const [name, args, re] of cases) {
    const r = await m.callTool(name, args);
    assert.equal(r.isError, true, `${name} ${JSON.stringify(args)}`);
    assert.match(r.content[0].text, re, `${name} ${JSON.stringify(args)}`);
  }
  assert.equal(probed, 0);
  assert.equal(spawned, 0);
});

test("IT-781: book_ids are validated, digit strings become integers, duplicates dropped", () => {
  const schema = m.TOOLS.find((t) => t.name === "search").inputSchema;
  assert.deepEqual(m.validateArgs(schema, { query: "x", book_ids: [173917, "143562", 173917] }), { query: "x", book_ids: [173917, 143562] });
});

test("IT-781 find_book: a leading shut / sefer prefix is recognized for the retry; other text is not", () => {
  for (const q of ["שו\"ת אגרות משה", "שו״ת אגרות משה", "שות אגרות משה", "ספר החינוך"]) assert.ok(m.BOOK_PREFIX_RE.test(q), q);
  for (const q of ["אגרות משה", "שותפות", "ספרי", "|ספר:משנה ברורה|"]) assert.ok(!m.BOOK_PREFIX_RE.test(q), q);
  assert.equal("שו\"ת אגרות משה".replace(m.BOOK_PREFIX_RE, ""), "אגרות משה");
});

// A Berachot-like book (type 2 per amud + type 1 headings) and a Mishna Berura-like one (type 1 only).
const BERACHOT = {
  refs: [["פסקי תוספות ברכות", 136, 1], ["ברכות ב א", 11, 2], ["ברכות ב ב", 12, 2], ["ברכות ג ב", 14, 2],
    ["ברכות י א", 27, 2], ["ברכות סד א", 135, 2], ["פירוש הרמב\"ם למשנה ב א", 147, 1]],
  pages: Array.from({ length: 150 }, (_, i) => [i + 1, `L${i + 1}`]),
};
const MB = {
  refs: [["מפתחות", 2, 1], ["הקדמה", 6, 1], ["סימן א", 8, 1], ["סימן ב", 12, 1], ["סימן אב", 90, 1], ["סימן קכג", 300, 1]],
  // page 300 is empty (filtered out): the next non-empty page is 302.
  pages: [...Array.from({ length: 299 }, (_, i) => [i + 1, i === 7 ? "ד א" : `P${i + 1}`]), [302, "קנא א"]],
};

test("IT-781 refVariants/normTitle: gershayim, nikud, amud dots, the word daf", () => {
  assert.deepEqual(m.refVariants("סימן קכ\"ג"), ["סימן קכג"]);
  assert.deepEqual(m.refVariants("דף יג."), ["דף יג א", "יג א"]);
  assert.deepEqual(m.refVariants("דף יג:"), ["דף יג ב", "יג ב"]);
  assert.deepEqual(m.refVariants("דף יג עמוד ב"), ["דף יג ב", "יג ב"]);
  assert.equal(m.normTitle("הִלְכוֹת  תְּשׁוּבָה"), "הלכות תשובה");
});

test("IT-781 go_to_reference: type 2 first, suffix match for Shas, next_reference", () => {
  const r = m.findReference(BERACHOT, "ב א");
  assert.equal(r.found, true);
  assert.equal(r.matched_title, "ברכות ב א"); // not the type-1 title that also ends with "ב א"
  assert.equal(r.type, 2);
  assert.equal(r.match, "suffix");
  assert.equal(r.page, 11);
  assert.equal(r.page_label, "L11");
  assert.deepEqual(r.next_reference, { title: "ברכות ב ב", page: 12, page_label: "L12" });
  assert.equal(m.findReference(BERACHOT, "דף ג:").matched_title, "ברכות ג ב");
  assert.equal(m.findReference(BERACHOT, "ברכות סד א").match, "exact");
});

test("IT-781 go_to_reference: exact / whole word before substring (siman alef is not siman alef-bet)", () => {
  const r = m.findReference(MB, "סימן א");
  assert.equal(r.matched_title, "סימן א");
  assert.equal(r.match, "exact");
  assert.equal(r.page, 8);
  assert.equal(r.page_label, "ד א");
  assert.deepEqual(r.next_reference, { title: "סימן ב", page: 12, page_label: "P12" });
  assert.ok(r.candidates.some((c) => c.title === "סימן אב" && c.match === "substring"));
  assert.equal(m.findReference(MB, "סימן קכ\"ג").matched_title, "סימן קכג");
});

test("IT-781 go_to_reference: a tsiyun on an empty page goes to the next non-empty page; label fallback; not found", () => {
  const r = m.findReference(MB, "סימן קכג");
  assert.equal(r.page, 302);
  assert.equal(r.page_label, "קנא א");
  assert.equal(r.next_reference, null);
  const noIndex = { refs: [], pages: [[1, "א"], [2, "יג א"], [3, "יג ב"]] };
  assert.deepEqual([m.findReference(noIndex, "דף יג").page, m.findReference(noIndex, "יג ב").page], [2, 3]);
  assert.equal(m.findReference(noIndex, "יג ב").match, "page_label");
  assert.equal(m.findReference(MB, "פרק ט").found, false);
});

test("IT-781 list_references: type 2 by default when the book has it, filter, paging", () => {
  const a = m.listReferences(BERACHOT, { limit: 2, offset: 0 });
  assert.deepEqual(a.type_counts, { catalogue: 5, headings: 2 });
  assert.equal(a.type_shown, 2);
  assert.equal(a.total_matching, 5);
  assert.deepEqual(a.references, [{ title: "ברכות ב א", page: 11, page_label: "L11" }, { title: "ברכות ב ב", page: 12, page_label: "L12" }]);
  assert.equal(a.next_offset, 2);
  const b = m.listReferences(MB, { filter: "סימן", limit: 100, offset: 0 });
  assert.equal(b.type_shown, "all");
  assert.deepEqual(b.references.map((x) => x.title), ["סימן א", "סימן ב", "סימן אב", "סימן קכג"]);
  assert.equal(b.references[0].type, 1);
  assert.equal(b.next_offset, null);
  assert.equal(m.listReferences(BERACHOT, { type: 1, limit: 10, offset: 0 }).total_matching, 2);
});

test("IT-781 hit context: window inside a page, across the previous page, cut at short pages, 200-word reads", () => {
  // pages of 100 words each, one part per page; page 3 has only 5 words.
  const pages = [[1, 0, 100], [2, 100, 100], [3, 200, 5], [4, 205, 300]].map(([position, firstWord, numWords]) =>
    ({ position, pagedata: [{ firstWord, numWords, letter: String(position) }] }));
  const segs = m.pageSegments(pages);
  let p = m.planHitWindow(segs, [150], 10);
  assert.deepEqual(p.ranges.map((r) => [r.seg, r.start, r.n]), [[1, 140, 21]]);
  assert.equal(p.cut, false);
  p = m.planHitWindow(segs, [103, 104], 10); // starts 3 words into page 2: 7 words from page 1
  assert.deepEqual(p.ranges.map((r) => [r.seg, r.start, r.n]), [[0, 93, 7], [1, 100, 15]]);
  assert.equal(p.seg, 1);
  assert.equal(p.cut, false);
  p = m.planHitWindow(segs, [2], 10); // book start
  assert.deepEqual(p.ranges.map((r) => [r.seg, r.start, r.n]), [[0, 0, 13]]);
  assert.equal(p.cut, false, "the book start is not a cut: there are no words before it");
  p = m.planHitWindow(segs, [210], 20); // page 3 (5 words) between: only one neighbour page is read
  assert.deepEqual(p.ranges.map((r) => [r.seg, r.start, r.n]), [[2, 200, 5], [3, 205, 26]]);
  assert.equal(p.cut, true);
  p = m.planHitWindow(segs, [300, 302, 400, 403, 450], 100); // a long hit group: reads split at 200
  assert.ok(p.ranges.every((r) => r.n <= 200));
  assert.equal(p.ranges.reduce((a, r) => a + r.n, 0), 305); // 5 words of page 3 + the 300 of page 4
  assert.equal(p.cut, false, "the book's end is not a cut");
  assert.equal(m.planHitWindow(segs, [9999], 5), null);
});

test("IT-781 hit context: hit words in guillemets, a page change marked, nearest word when the index has no word", () => {
  const P1 = {}, P2 = {};
  const t = m.hitText([{ page: P1, list: [{ w: "א", abs: 98 }, { w: "ב", abs: 99 }] },
    { page: P2, list: [{ w: "ג", abs: 100 }, { w: "ד", abs: 101 }, { w: "ה", abs: 102 }] }], [100, 101]);
  assert.equal(t, "א ב ‖ «ג» «ד» ה");
  assert.equal(m.hitText([{ page: P1, list: [{ w: "א", abs: 5 }, { w: "ב", abs: 8 }] }], [6]), "א «ב»");
  assert.deepEqual(m.groupOffsets([1, 2, 3, 10, 12, 30]), [[1, 2, 3], [10, 12], [30]]);
});

// ---- IT-779 review notes, fixed in IT-781 ----
const freeLoopbackPort = () => new Promise((ok) => { const s = net.createServer(); s.listen(0, "127.0.0.1", () => { const p = s.address().port; s.close(() => ok(p)); }); });

test("IT-781 N3: debugging ports are matched exactly (6056 is not 60568)", async () => {
  assert.deepEqual(m.cmdPorts([{ cmd: "otzar.exe --remote-debugging-port=60568" }, { cmd: "x --remote-debugging-port=6056 --y" }, { cmd: "" }]), [60568, 6056]);
  assert.deepEqual(m.cmdPorts([{ cmd: "otzar.exe --remote-debugging-port=123456" }]), []);
  reset();
  const mc = fakeMachine();
  m.state.lastStartPort = 6056;
  m.state.noAnswer = true;
  mc.extra = { pid: 9, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=60568` }; // someone else's, not answering
  await assert.rejects(m.ensureOtzar({ waitForWindow: false, loadingWaitMs: 1200 }), (e) => e.message === m.MSG_STILL_LOADING);
  assert.equal(mc.spawns, 0);
});

test("IT-781 N2: an Otzar opened by another launch is still loading -> wait for it, no second start, no 'close it' message", async () => {
  reset();
  const mc = fakeMachine();
  const port = await freeLoopbackPort();
  mc.extra = { pid: 88, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${port}` };
  let late = null;
  setTimeout(() => fakeCdp(UA_OTZAR, port).then((s) => { late = s; }), 1500); // answers after 1.5 s
  try {
    assert.equal(await m.ensureOtzar({ waitForWindow: false, loadingWaitMs: 8000 }), port);
    assert.equal(mc.spawns, 0);
  } finally { if (late) await closeSrv(late); }
});

test("IT-781 N2: launch --shortcut while another start is loading -> waits, brings it to the front, no popup", async () => {
  reset();
  const sh = fakeShell();
  const mc = fakeMachine();
  const port = await freeLoopbackPort();
  mc.extra = { pid: 99, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${port}` };
  let late = null;
  setTimeout(() => fakeCdp(UA_OTZAR, port).then((s) => { late = s; }), 1500);
  try {
    assert.equal(await m.launchFromShortcut(), 0);
    assert.equal(mc.spawns, 0);
    assert.deepEqual(sh.activated, [[99]]);
    assert.equal(sh.popups.length, 0);
  } finally { if (late) await closeSrv(late); }
});

test("IT-781 N2: the start lock is held by one process at a time; a second launch waits for it and then connects instead of starting", async () => {
  const a = await m.tryLock(TEST_LOCK);
  assert.ok(a, "first lock");
  assert.equal(await m.tryLock(TEST_LOCK), null, "a second holder is refused by Windows");
  // Another launcher (simulated by `a`) is starting Otzar. While it holds the lock, this one waits.
  reset();
  const mc = fakeMachine();
  let user = null;
  setTimeout(async () => {
    user = await fakeCdp(UA_OTZAR);
    mc.extra = { pid: 7, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${user.address().port}` };
    a.close(); // the other launcher is done: its Otzar answers
  }, 1200);
  const t0 = Date.now();
  try {
    const port = await m.ensureOtzar({ waitForWindow: false });
    assert.ok(Date.now() - t0 >= 1000, "waited for the lock");
    assert.equal(port, user.address().port);
    assert.equal(mc.spawns, 0, "no second Otzar");
  } finally { if (user) await closeSrv(user); }
  const again = await m.tryLock(TEST_LOCK);
  assert.ok(again, "the lock was released after the call");
  again.close();
});

test("IT-781 N1: the shortcut popup is not closed after 30 s; the loading message is Hebrew only", () => {
  assert.ok(m.POPUP_TIMEOUT_MS >= 60 * 60_000);
  const t = m.shortcutPopupText(new m.UserError(m.MSG_STILL_LOADING));
  assert.match(t, /עדיין נפתח/);
  assert.doesNotMatch(t, /[A-Za-z]{3,}/);
});

// =============================================================================================
// IT-788: the book index on any disk version, and a start without the Otzar data disk
// =============================================================================================
const zlib = require("node:zlib");
const fs = require("node:fs");

// The page function of bookData(), run here in Node against a fake AXIOS_OFFLINE. Node has the
// same TextDecoder / DecompressionStream / Blob / Response as Otzar's page. A fake webpack runtime
// carries a pako-like module (inflate detects zlib and gzip, as pako does); it can be removed to
// test our own fallback decoders.
const PAKO = {
  inflate: (x) => new Uint8Array(x[0] === 0x1f && x[1] === 0x8b ? zlib.gunzipSync(x) : zlib.inflateSync(x)),
  inflateRaw: (x) => new Uint8Array(zlib.inflateRawSync(x)),
  ungzip: (x) => new Uint8Array(zlib.gunzipSync(x)),
};
const fakeReq = { c: {} };
globalThis.webpackJsonp = { push: ([[id], mods]) => mods[id]({}, {}, fakeReq) };
async function runPage(data, { otzarDecoder = true, getFails = false } = {}) {
  if (otzarDecoder) fakeReq.c.d7ac = { exports: PAKO }; else delete fakeReq.c.d7ac;
  fakeReq.c.other = { exports: { inflate: () => { throw new Error("not this one"); } } }; // only inflate: never chosen
  globalThis.AXIOS_OFFLINE = { get: async (route, opts) => {
    assert.equal(route, "api/books/data/25882");
    assert.equal(opts.responseType, "arraybuffer");
    if (getFails) throw new Error("404");
    return { data };
  } };
  try { return await (0, eval)(`(${m.BOOK_DATA_PAGE_FN})`)("api/books/data/25882"); } finally { delete globalThis.AXIOS_OFFLINE; }
}
const DATA = [[{ id: 25882, moredata: {} }],
  [{ position: 1, pagedata: [{ letter: "א" }] }, { position: 2, empty: true }, { position: 3, pagedata: [{ letter: "ב" }] }],
  [{ title: "סימן א", page: 1, type: 1 }, { title: "סימן ב", page: 3, type: 1 }], []];
const JSON_BYTES = Buffer.from(JSON.stringify(DATA), "utf8");
const ab = (b) => b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength);
function assertData(r) {
  assert.deepEqual(r.refs, [["סימן א", 1, 1], ["סימן ב", 3, 1]]);
  assert.deepEqual(r.pages, [[1, "א"], [3, "ב"]]);
}

test("IT-788 book index: Otzar's own decoder (found by export names) reads zlib (v19), gzip, plain JSON and raw deflate", async () => {
  for (const body of [zlib.deflateSync(JSON_BYTES), zlib.gzipSync(JSON_BYTES), JSON_BYTES]) {
    const r = await runPage(ab(body));
    assertData(r);
    assert.equal(r.diag.decoder, "otzar");
    assert.equal(r.diag.otzar_decoder_found, true);
  }
  const raw = await runPage(ab(zlib.deflateRawSync(JSON_BYTES)));
  assertData(raw);
  assert.equal(raw.diag.decoder, "otzar-inflateRaw");
});

test("IT-788 book index: without Otzar's decoder, our fallbacks read zlib (any window), gzip, raw deflate, plain JSON, a BOM", async () => {
  const cases = [
    [zlib.deflateSync(JSON_BYTES), "zlib"],
    [zlib.deflateSync(JSON_BYTES, { windowBits: 13 }), "zlib"], // header 0x58, not 0x78
    [zlib.gzipSync(JSON_BYTES), "gzip"],
    [zlib.deflateRawSync(JSON_BYTES), "deflate-raw"],
    [JSON_BYTES, "plain"],
    [Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), JSON_BYTES]), "plain"],
  ];
  for (const [body, decoder] of cases) {
    const r = await runPage(new Uint8Array(body), { otzarDecoder: false });
    assertData(r);
    assert.equal(r.diag.decoder, decoder, decoder);
    assert.equal(r.diag.otzar_decoder_found, false);
  }
  assertData(await runPage(DATA, { otzarDecoder: false })); // already parsed by Axios
  const s = await runPage(JSON.stringify(DATA), { otzarDecoder: false }); // JSON text as a string
  assertData(s);
  assert.equal(s.diag.decoder, "string-json");
  assertData(await runPage(zlib.deflateSync(JSON_BYTES).toString("latin1"))); // binary string, one char per byte
});

test("IT-788 book index: an unknown format -> diagnostics (first bytes, length, tried), never a raw SyntaxError", async () => {
  const junk = Buffer.from([0x4f, 0x54, 0x5a, 0x01, 0x02, 0x03, 0xff, 0xfe, 0x00, 0x10]);
  const r = await runPage(ab(junk));
  assert.equal(r.decodeFailed, true);
  assert.equal(r.diag.length, 10);
  assert.equal(r.diag.head_hex, "4f 54 5a 01 02 03 ff fe 00 10");
  assert.ok(r.diag.tried.length >= 3);
  const e = m.bookDataError(25882, { ...r.diag, otzar_disk_version: 21 });
  assert.ok(e instanceof m.UserError);
  assert.equal(e.code, "book_data_format");
  assert.match(e.message, /^Could not read the index \(tsiyunim\) of book 25882/);
  assert.match(e.message, /disk version 21/);
  assert.match(e.message, /first bytes \[4f 54 5a 01/);
  assert.match(e.message, /doctor --book 25882/);
  assert.equal((await runPage(null, { getFails: true })).missing, true);
  assert.equal((await runPage(new ArrayBuffer(0))).missing, true);
});

test("IT-788 book index: a text book (moredata.isText) is flagged, so its pages come from the page list", async () => {
  const txt = [[{ id: 5, moredata: { isText: 1 } }]];
  const r = await runPage(ab(zlib.deflateSync(Buffer.from(JSON.stringify(txt)))));
  assert.equal(r.isText, true);
  assert.deepEqual(r.refs, []);
  assert.deepEqual(r.pages, []);
});

test("IT-788 disk: Otzar's own rule (data\\otzar_ver.txt); network mode needs no disk; unknown setups never block", async () => {
  assert.deepEqual(m.DISK_MARKERS, ["data\\otzar_ver.txt", "otzarDisk\\data\\otzar_ver.txt"]);
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "otzar-mcp-test-"));
  try {
    const local = path.join(root, "otzarApp", "otzarLocal");
    const exe = path.join(local, "launcher", "bin", "x64", "app", "otzar.exe");
    fs.mkdirSync(path.dirname(exe), { recursive: true });
    assert.equal(m.otzarLocalDir(exe), local);
    fs.writeFileSync(path.join(local, "onet.otzar"), "");
    assert.equal((await m.otzarDiskState({ exe })).state, "network");
    assert.equal((await m.otzarDiskState({ exe: "Z:\\somewhere\\otzar.exe" })).state, "unknown");
    assert.equal((await m.otzarDiskState(null)).state, "unknown");
    process.env.OTZAR_MCP_SKIP_DISK_CHECK = "1";
    assert.equal((await m.otzarDiskState({ exe })).state, "skipped");
  } finally { delete process.env.OTZAR_MCP_SKIP_DISK_CHECK; fs.rmSync(root, { recursive: true, force: true }); }
});

// IT-788 review r1: the real scan policy (scanDrives), with a fake probe per drive. "hang" never
// answers (a dead drive); the policy's own limit must skip it.
const fakeProbe = (answers, calls = []) => (d) => { calls.push(d); const a = answers[d]; return a === "hang" ? new Promise(() => {}) : Promise.resolve(a); };

test("IT-788 disk scan: a drive that hangs is skipped; a marker on another drive is 'present' without waiting for it", async () => {
  const calls = [];
  const t0 = Date.now();
  const r = await m.scanDrives(["C:", "Z:", "E:"], fakeProbe({ "C:": "no", "Z:": "hang", "E:": "yes" }, calls), 1500);
  assert.deepEqual(r, { state: "present", drives: ["E:"] });
  assert.ok(Date.now() - t0 < 1000, "did not wait for the hanging drive");
  assert.deepEqual(calls, ["C:", "Z:", "E:"], "every drive is checked, also the ones after a hanging drive");
  // The hanging drive comes first and the marker last: still present.
  assert.equal((await m.scanDrives(["Z:", "Y:", "E:"], fakeProbe({ "Z:": "hang", "Y:": "hang", "E:": "yes" }), 1500)).state, "present");
  // A probe that answers "timeout" by itself is skipped the same way.
  assert.equal((await m.scanDrives(["Z:", "E:"], fakeProbe({ "Z:": "timeout", "E:": "yes" }), 1500)).state, "present");
});

test("IT-788 disk scan: drives that hang plus no marker anywhere -> 'missing' (blocks), not 'unknown'", async () => {
  const t0 = Date.now();
  const r = await m.scanDrives(["C:", "Z:", "D:"], fakeProbe({ "C:": "no", "Z:": "hang", "D:": "no" }), 1500);
  assert.equal(r.state, "missing");
  assert.deepEqual(r.skipped_drives, ["Z:"]);
  assert.ok(Date.now() - t0 < 1500 + 500 + 1000, "bounded by one drive's limit, not the sum");
  assert.deepEqual(await m.scanDrives(["C:", "D:"], fakeProbe({ "C:": "no", "D:": "no" }), 1500), { state: "missing" });
  // Only a check that could not run is 'unknown': a probe that fails, a probe that throws, no drives.
  assert.equal((await m.scanDrives(["C:", "D:"], fakeProbe({ "C:": "no", "D:": "error" }), 1500)).state, "unknown");
  assert.equal((await m.scanDrives(["C:"], () => { throw new Error("spawn failed"); }, 1500)).state, "unknown");
  assert.equal((await m.scanDrives([], fakeProbe({}), 1500)).state, "unknown");
  assert.equal(m.DISK_PROBE_MS, 5000);
});

test("IT-788 disk scan: the real per-drive probe reads the markers and refuses a bad drive name", async () => {
  // A real cmd.exe child on the system drive (read-only "if exist"): this PC's system drive has no
  // Otzar marker at its root, so "no" (or "yes" if it has); never an error or a hang.
  const sys = (process.env.SystemDrive || "C:").toUpperCase();
  assert.ok(["no", "yes"].includes(await m.probeDrive(sys, 10_000)));
  assert.equal(await m.probeDrive("C:\" & echo YES & \"", 1000), "error");
  assert.equal(await m.probeDrive("CC:", 1000), "error");
});

test("IT-788 disk: no Otzar disk -> Otzar is not started, clear message, no start counted; 'unknown' never blocks", async () => {
  reset();
  const mc = fakeMachine();
  m.deps.diskState = () => ({ state: "missing" });
  await assert.rejects(m.ensureOtzar({ waitForWindow: false }),
    (e) => e instanceof m.UserError && e.code === "no_disk" && e.message === m.MSG_NO_DISK && /connect the Otzar disk/.test(e.message));
  assert.equal(mc.spawns, 0);
  assert.deepEqual(m.state.starts, []);
  assert.equal(m.state.noAnswer, false);
  m.deps.diskState = () => ({ state: "unknown", reason: "test" });
  try { await m.ensureOtzar({ waitForWindow: false }); assert.equal(mc.spawns, 1); } finally { await mc.close(); }
});

test("IT-788 disk: the shortcut without the disk -> a Hebrew-only popup, nothing started", async () => {
  reset();
  const sh = fakeShell();
  const mc = fakeMachine();
  m.deps.diskState = () => ({ state: "missing" });
  assert.equal(await m.launchFromShortcut(), 2);
  assert.equal(mc.spawns, 0);
  assert.equal(sh.popups.length, 1);
  assert.match(sh.popups[0], /^הדיסק של אוצר החכמה לא מחובר למחשב/);
  assert.match(sh.popups[0], /חברו את הדיסק/);
  assert.doesNotMatch(sh.popups[0], /[A-Za-z]{3,}/);
});

test("IT-788: an Otzar that started long ago and never answers is 'stuck', not 'still loading', at once", async () => {
  reset();
  const mc = fakeMachine();
  const port = await freeLoopbackPort(); // nothing listens: never answers
  mc.extra = { pid: 66, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${port}`, created: mc.now - m.STUCK_AFTER_MS - 1000 };
  const t0 = Date.now();
  await assert.rejects(m.ensureOtzar({ waitForWindow: false, loadingWaitMs: 8000 }),
    (e) => e.code === "not_answering" && /stuck/.test(e.message) && /restart the PC/.test(e.message) && !/disk is not connected/.test(e.message));
  assert.ok(Date.now() - t0 < 1000, "no loading wait");
  m.deps.diskState = () => ({ state: "missing" });
  await assert.rejects(m.ensureOtzar({ waitForWindow: false }), (e) => e.code === "not_answering_no_disk" && /data disk is not connected/.test(e.message));
  assert.equal(mc.spawns, 0, "never started again");
  // The same through the shortcut: Hebrew only, says to check the disk and to restart the PC.
  const sh = fakeShell();
  assert.equal(await m.launchFromShortcut(), 2);
  assert.match(sh.popups[0], /^הדיסק של אוצר החכמה לא מחובר למחשב/);
  assert.match(sh.popups[0], /לא מגיב/);
  assert.match(sh.popups[0], /הפעילו מחדש את המחשב/);
  assert.doesNotMatch(sh.popups[0], /[A-Za-z]{3,}/);
  m.deps.diskState = DISK_PRESENT;
  assert.doesNotMatch(m.shortcutPopupText(await m.notAnsweringError(INSTALL, m.STUCK_AFTER_MS)), /[A-Za-z]{3,}|לא מחובר למחשב/);
});

test("IT-788: the loading wait ends when the Otzar becomes 'stuck'; a young one is still 'still loading'", async () => {
  reset();
  const mc = fakeMachine();
  const port = await freeLoopbackPort();
  mc.extra = { pid: 67, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${port}`, created: mc.now - (m.STUCK_AFTER_MS - 1500) };
  const t0 = Date.now();
  await assert.rejects(m.ensureOtzar({ waitForWindow: false, loadingWaitMs: 8000 }), (e) => e.code === "not_answering");
  assert.ok(Date.now() - t0 < 4500, "waited only until it counted as stuck");
  mc.extra.created = mc.now - 10_000; // started 10 s ago
  await assert.rejects(m.ensureOtzar({ waitForWindow: false, loadingWaitMs: 1200 }), (e) => e.message === m.MSG_STILL_LOADING);
  assert.equal(mc.spawns, 0);
});

test("IT-788: the starter answers but there is no main window and no disk -> 'connect the disk', not 'still loading'", async () => {
  reset();
  const starter = await fakeCdp(UA_OTZAR); // its /json/list is []: no main window
  try {
    m.state.port = starter.address().port;
    m.state.checked = true;
    m.deps.otzarProcesses = () => [{ pid: 3, exe: INSTALL, cmd: `"${INSTALL}" --remote-debugging-port=${m.state.port}` }];
    m.deps.diskState = () => ({ state: "missing" });
    const r = await m.callTool("book_info", { book_id: 25882 });
    assert.equal(r.isError, true);
    assert.match(r.content[0].text, /data disk is not connected/);
    m.deps.diskState = DISK_PRESENT;
    const r2 = await m.callTool("book_info", { book_id: 25882 });
    assert.match(r2.content[0].text, /still loading/);
  } finally { m.state.port = null; await closeSrv(starter); }
});

// ---------------------------------------------------------------------------------------------
// IT-834: find_book on disk 24. The page function of findBooksOnce() runs here in Node against a
// fake Vue root (document #app) and a fake webpack module that exports searchByTxt. The fake
// searchByTxt plays the disk 19 behaviour (reads searchResultConfig, writes tsiyunSearch and
// hiddenBooks) and the disk 24 cases (reads more fields, writes into nested ones, or reads a field
// the state does not have).
// ---------------------------------------------------------------------------------------------
const BOOK = (id, name, extra = {}) => ({ id, name, mainVolume: id, volumes_length: 1, authors_length: 1, authors_0_name: "מחבר", _show: true, ...extra });
function realBookList() {
  // Like Vue 2 state: reactive getters/setters, a non-enumerable __ob__, nested objects and arrays.
  const st = {};
  const fields = {
    searchResultConfig: { full_points: 1, levinMin: 0.8, score: { name: { score: 25, bit: 1 } } },
    hiddenBooks: 7,
    tsiyunSearch: "",
    systemBasicBooksIds: [1, 2, 3],
    historyUsersActions: { current: 1, data: [{ type: "ALL", content: "" }] },
  };
  for (const [k, v] of Object.entries(fields)) {
    let val = v;
    Object.defineProperty(st, k, { enumerable: true, configurable: true, get: () => val, set: (x) => { val = x; } });
  }
  Object.defineProperty(st, "__ob__", { enumerable: false, value: { dep: { subs: [() => {}] } } });
  return st;
}
// A fake of Otzar's tiny-index module (update 7.40001, module default export): searchTinyIndex
// walks the module-level index `d`, which starts as {} (as in Otzar: "var d={}") and is filled by
// the store action books/loadIndexData(true), the one Otzar's F2 input dispatches on focus.
function fakeTinyIndex({ loaded = true, loadFixes = true } = {}) {
  const ix = { d: loaded ? { buffer: new ArrayBuffer(8), strings: ["משנה", "ברורה", "אגרות"] } : {}, dispatches: [] };
  ix.module = { exports: { default: {
    createTinyIndex: () => {},
    searchTinyIndex: (words, cfg = {}) => {
      const n = ix.d; new Int32Array(n.buffer); // like Otzar: null -> "reading 'buffer'"
      words.forEach((e) => { e.word = e.word.toLowerCase(); n.strings.forEach((s) => { s.indexOf(e.word); if (e.matchWordComplete) return; }); });
      return [];
    },
    inxData: {},
  } } };
  ix.dispatch = async (name, arg) => {
    ix.dispatches.push([name, arg]);
    if (name === "books/loadIndexData" && arg === true && loadFixes) ix.d = { buffer: new ArrayBuffer(8), strings: ["משנה", "ברורה", "אגרות"] };
  };
  return ix;
}
async function withFakePage(searchByTxt, bookList, fn, ix = fakeTinyIndex()) {
  fakeReq.c.bookListModule = { exports: { searchByTxt } };
  if (ix) fakeReq.c.tinyIndexModule = ix.module;
  const root = { $store: { state: { bookList, otzar_version: 24, offline: { licence: { otzarVer: 24, updatesVer: "7.40001" } } },
    dispatch: ix ? ix.dispatch : async () => {} } };
  globalThis.document = { querySelector: (q) => (q === "#app" ? { __vue__: root } : null), querySelectorAll: () => [] };
  try { return await fn(); } finally { delete globalThis.document; delete fakeReq.c.bookListModule; delete fakeReq.c.tinyIndexModule; }
}
async function runFindPage(searchByTxt, { text = "משנה ברורה", limit = 20, diag = false, bookList = realBookList(), ix } = {}) {
  const before = JSON.stringify(bookList);
  const r = await withFakePage(searchByTxt, bookList, () => (0, eval)(`(${m.FIND_BOOK_PAGE_FN})`)(text, limit, diag), ix);
  return { r, before, after: JSON.stringify(bookList), bookList };
}
// Disk 19/21: what searchByTxt does with the state it gets.
function searchV19(text, special, n) {
  assert.equal(n.searchResultConfig.levinMin, 0.8);
  n.tsiyunSearch = text.split("  ")[1] || "";
  n.hiddenBooks = 0;
  n.hiddenBooks++;
  return [{ id: 0, special: true, allVolumes: false, book: BOOK(25882, "משנה ברורה") }];
}

test("IT-834 find_book page: disk 19 behaviour still works, only searchResultConfig is copied, the real state is unchanged", async () => {
  const { r, before, after } = await runFindPage(searchV19, { text: "משנה ברורה  סימן א", diag: true });
  assert.equal(r.failed, undefined);
  assert.equal(r.total, 1);
  assert.equal(r.books[0].book_id, 25882);
  assert.equal(r.hidden, 1);
  assert.equal(r.ref, "סימן א");
  assert.deepEqual(r.state_reads, { searchResultConfig: "copied", tsiyunSearch: "written first", hiddenBooks: "written first" });
  assert.equal(after, before, "the real bookList state did not change");
});

test("IT-834 find_book page: a field read on disk 24 is there (copied), and writes into nested objects/arrays never reach the real state", async () => {
  const v24 = (text, special, n) => {
    n.systemBasicBooksIds.forEach(() => {});
    n.systemBasicBooksIds.push(99);
    n.searchResultConfig.score.name.score = 0;
    n.historyUsersActions.data[0].content = "changed";
    n.historyUsersActions = null;
    delete n.tsiyunSearch;
    Object.defineProperty(n, "extra", { value: 1, configurable: true });
    n.hiddenBooks = 0;
    return [{ id: 0, special: true, book: BOOK(1, "א") }];
  };
  const { r, before, after, bookList } = await runFindPage(v24, { diag: true });
  assert.equal(r.failed, undefined, JSON.stringify(r.error));
  assert.equal(r.total, 1);
  assert.equal(after, before, "the real bookList state did not change");
  assert.deepEqual(bookList.systemBasicBooksIds, [1, 2, 3]);
  assert.equal(bookList.searchResultConfig.score.name.score, 25);
  assert.equal(bookList.historyUsersActions.data[0].content, "");
  assert.equal(bookList.tsiyunSearch, "");
  assert.equal("extra" in bookList, false);
  assert.equal(r.state_reads.systemBasicBooksIds, "copied");
  assert.equal(r.state_reads.searchResultConfig, "copied");
  assert.equal(r.state_reads.hiddenBooks, "written first", "a field only written is never copied");
  assert.equal(r.otzar_disk_version, 24);
});

test("IT-834 find_book page: enumerating the state ({...n}) sees every real field, as copies", async () => {
  const spread = (text, special, n) => { const c = { ...n }; c.systemBasicBooksIds.push(5); assert.ok("historyUsersActions" in n); return []; };
  const { r, before, after } = await runFindPage(spread);
  assert.equal(r.failed, undefined, JSON.stringify(r.error));
  assert.equal(r.total, 0);
  assert.equal(after, before);
});

test("IT-834 find_book page: a field missing from the state gives the error as data, with the missing field, never a throw", async () => {
  const v24missing = (text, special, n) => { n.searchResultConfig; n.newDisk24List.forEach(() => {}); return []; };
  const { r, before, after } = await runFindPage(v24missing, { diag: true });
  assert.equal(r.failed, true);
  assert.equal(r.error.name, "TypeError");
  assert.match(r.error.message, /reading 'forEach'/);
  assert.ok(Array.isArray(r.error.stack) && r.error.stack.length > 0);
  assert.equal(r.state_reads.newDisk24List, "missing");
  assert.equal(r.state_reads.searchResultConfig, "copied");
  assert.ok(r.real_state_fields.includes("searchResultConfig"));
  assert.ok(r.search_fn_length > 0);
  assert.equal(after, before);
});

test("IT-834 find_book page: a function in the state is not handed over; reading the result is guarded too", async () => {
  const bookList = realBookList();
  bookList.fn = () => "real";
  const usesFn = (text, special, n) => { assert.equal(n.fn, undefined); return [null]; };
  const { r } = await runFindPage(usesFn, { bookList, diag: true });
  assert.equal(r.failed, true);
  assert.equal(r.stage, "result");
  assert.equal(r.state_reads.fn, "not copyable");
});

test("IT-834 findBookError: a plain message with the JS error, the missing fields and the way around it", () => {
  const e = m.findBookError({ error: { name: "TypeError", message: "Cannot read properties of undefined (reading 'forEach')" },
    state_reads: { searchResultConfig: "copied", newDisk24List: "missing" } });
  assert.ok(e instanceof m.UserError);
  assert.equal(e.code, "find_book_failed");
  assert.match(e.message, /^Otzar's book-name search failed in this Otzar version \(TypeError: Cannot read properties of undefined \(reading 'forEach'\)\)/);
  assert.match(e.message, /state fields it needed that are not in Otzar's book list: newDisk24List \(missing\)/);
  assert.doesNotMatch(e.message, /searchResultConfig/);
  assert.match(e.message, /use search with the book's name instead/);
  assert.match(e.message, /doctor --find-book/);
  const e2 = m.findBookError({ error: { name: "TypeError", message: "x" }, state_reads: {} });
  assert.doesNotMatch(e2.message, /state fields/);
});

test("IT-834 findBooksOnce: a failed search is a UserError; a lost CDP connection stays a CDP error; diag returns the data", async () => {
  const sess = (fn) => ({ evaluate: async (expr) => withFakePage(fn, realBookList(), () => (0, eval)(expr)) });
  const bad = (t, s, n) => n.nothing.forEach(() => {});
  await assert.rejects(m.findBooksOnce(sess(bad), "משנה ברורה", 20), (e) => e instanceof m.UserError && e.code === "find_book_failed" && /nothing \(missing\)/.test(e.message));
  const d = await m.findBooksOnce(sess(bad), "משנה ברורה", 5, true);
  assert.equal(d.failed, true);
  const ok = await m.findBooksOnce(sess(searchV19), "משנה ברורה", 20);
  assert.equal(ok.books[0].name, "משנה ברורה");
  await assert.rejects(m.findBooksOnce({ evaluate: async () => { throw new Error("CDP connection closed"); } }, "x", 1), (e) => !(e instanceof m.UserError) && e.message === "CDP connection closed");
  await assert.rejects(m.findBooksOnce({ evaluate: async () => { throw new Error("TypeError: boom"); } }, "x", 1), (e) => e.code === "find_book_failed" && /book-name search failed/.test(e.message));
  await assert.rejects(m.findBooksOnce({ evaluate: async () => ({ missing: true }) }, "x", 1), /was not found in this Otzar version/);
});

test("IT-834 bundleFrame: finds the first frame in Otzar's own bundle, ignores others", () => {
  assert.deepEqual(m.bundleFrame([
    "TypeError: Cannot read properties of undefined (reading 'forEach')",
    "at eval (eval at <anonymous>, <anonymous>:3:5)",
    "at H (file:///C:/otzarApp/OtzarLocal/launcher/bin/x64/dist/js/app.3b803a52.js:1:2480200)",
  ]), { url: "file:///C:/otzarApp/OtzarLocal/launcher/bin/x64/dist/js/app.3b803a52.js", line: 1, column: 2480200, file: "app.3b803a52.js" });
  assert.equal(m.bundleFrame(["at H (https://evil.example/app.js:1:2)"]), null);
  assert.equal(m.bundleFrame(undefined), null);
  assert.equal(m.bundleFrame(["at w (app://./js/app.1.js:1:20)"]).column, 20);
});

// IT-834 round 2: the book-name index. The fake searchByTxt is shaped like Otzar's H: it reads only
// searchResultConfig and calls the tiny-index module's searchTinyIndex, which walks the index.
function searchLikeH(ix) {
  const calls = { n: 0 };
  const fn = (text, special, n) => {
    calls.n++;
    const cfg = n.searchResultConfig;
    ix.module.exports.default.searchTinyIndex([{ word: text.split(" ")[0], wordAt: 0, inCol: [], mainWord: true }], cfg);
    n.tsiyunSearch = ""; n.hiddenBooks = 0;
    return [{ id: 0, special: true, book: BOOK(25882, "משנה ברורה") }];
  };
  return { fn, calls };
}

test("IT-834 r2: index not loaded ({} as at Otzar start) -> Otzar's own loadIndexData(true), as F2 does, then the search works", async () => {
  const ix = fakeTinyIndex({ loaded: false });
  const h = searchLikeH(ix);
  const { r, before, after } = await runFindPage(h.fn, { ix, diag: true });
  assert.equal(r.failed, undefined, JSON.stringify(r.error));
  assert.equal(r.books[0].book_id, 25882);
  assert.deepEqual(ix.dispatches, [["books/loadIndexData", true]]);
  assert.equal(r.index.before, "not loaded (strings missing)");
  assert.equal(r.index.after, "loaded");
  assert.equal(r.index.loaded_by_otzar_mcp, true);
  assert.equal(r.index.strings_length, 3, "counted by the probe, no word read back");
  assert.equal(r.index.otzar_version, 24);
  assert.equal(h.calls.n, 1);
  assert.equal(after, before, "the real bookList state did not change");
});

test("IT-834 r2: index already loaded -> no dispatch at all; null index (freed) is also loaded the F2 way", async () => {
  const ix = fakeTinyIndex();
  const { r } = await runFindPage(searchLikeH(ix).fn, { ix, diag: true });
  assert.equal(r.failed, undefined);
  assert.deepEqual(ix.dispatches, []);
  assert.deepEqual([r.index.before, r.index.after, r.index.strings_length], ["loaded", undefined, 3]);
  const ix2 = fakeTinyIndex(); ix2.d = null;
  const { r: r2 } = await runFindPage(searchLikeH(ix2).fn, { ix: ix2, diag: true });
  assert.equal(r2.failed, undefined);
  assert.equal(r2.index.before, "not loaded (null)");
  assert.equal(r2.index.after, "loaded");
});

test("IT-834 r2 (review item 4): searchByTxt reads only searchResultConfig and throws reading 'forEach' -> the error and the doctor payload name the index", async () => {
  // a) the index cannot be loaded: searchByTxt is not even called; the index is named.
  const ix = fakeTinyIndex({ loaded: false, loadFixes: false });
  let called = 0;
  const thrower = (t, s, n) => { called++; n.searchResultConfig; const d = {}; return d.strings.forEach(() => {}); };
  const sess = (fn, ixx) => ({ evaluate: async (expr) => withFakePage(fn, realBookList(), () => (0, eval)(expr), ixx) });
  const bookList = realBookList();
  const { r, before, after } = await runFindPage(thrower, { ix, diag: true, bookList });
  assert.equal(r.failed, true);
  assert.equal(r.stage, "index");
  assert.equal(called, 0);
  assert.deepEqual(r.index, { before: "not loaded (strings missing)", loaded_by_otzar_mcp: true, otzar_version: 24, after: "not loaded (strings missing)", strings_length: null });
  assert.equal(Object.values(r.state_reads).includes("missing"), false, "no book-list field is blamed");
  assert.equal(after, before, "the real bookList state did not change");
  await assert.rejects(m.findBooksOnce(sess(thrower, fakeTinyIndex({ loaded: false, loadFixes: false })), "משנה ברורה", 20), (e) => {
    assert.equal(e.code, "find_book_failed");
    assert.match(e.message, /book-name index: not loaded \(strings missing\), after Otzar's loadIndexData: not loaded \(strings missing\), otzar_version 24/);
    assert.doesNotMatch(e.message, /state fields/);
    assert.match(e.message, /use search with the book's name instead/);
    return true;
  });
  // b) the probe finds the index loaded, but searchByTxt still throws the same TypeError: the
  //    error is the real one, and it still reports the index (loaded, its word count).
  const ix3 = fakeTinyIndex();
  const r3 = await m.findBooksOnce(sess(thrower, ix3), "משנה ברורה", 5, true);
  assert.equal(r3.failed, true);
  assert.match(r3.error.message, /reading 'forEach'/);
  assert.deepEqual([r3.index.before, r3.index.strings_length], ["loaded", 3]);
  await assert.rejects(m.findBooksOnce(sess(thrower, fakeTinyIndex()), "משנה ברורה", 20), /\(TypeError: Cannot read properties of undefined \(reading 'forEach'\)\); book-name index: loaded, otzar_version 24\. find_book cannot/);
});

test("IT-834 r2: no tiny-index module in this Otzar -> searchByTxt is still tried, and the index is reported as 'module not found'", async () => {
  const { r } = await runFindPage(searchV19, { ix: null, diag: true });
  assert.equal(r.failed, undefined);
  assert.equal(r.index.before, "module not found");
  assert.equal(r.index.loaded_by_otzar_mcp, false);
});

// ---- IT-876: an unscoped search covers the whole library, whatever scope is selected in Otzar ----
// A fake search panel: one fs_input_ element whose Vue component has the free-search store, the
// scope the user chose (searchInType/searchInBooks) and a doFreeSearch that records its calls.
const EN = { all: 1, selected: 3, inMeforesh: 4, browse: 6, currentList: 2, tsiyunim: 5, custom: 7 };
function fakeSearchPanel({ searchInType = 1, searchInBooks = [], searchType = "classic", noComp = false, savedListName = "" } = {}) {
  const calls = [];
  const store = { searchInType, searchInBooks, savedListName, setBooksToSearchIn() { throw new Error("must not change the selection"); } };
  const fs = { enumSearchInType: EN, searchType };
  const comp = { freeSearchBookListStore: store, doFreeSearch: (...a) => { calls.push({ args: a, searchType: fs.searchType }); return Promise.resolve(); } };
  const input = { id: "fs_input_0.1", __vue__: noComp ? undefined : comp, parentElement: null };
  const root = { $store: { state: { freeSearch: fs }, getters: {} } };
  const doc = { querySelector: (q) => (q === "#app" ? { __vue__: root } : null), querySelectorAll: () => [input] };
  return { calls, store, fs, doc };
}
function runSubmit(panel, q, ids) {
  globalThis.document = panel.doc;
  try { return (0, eval)(`(${m.SEARCH_SUBMIT_PAGE_FN})`)(q, ids); } finally { delete globalThis.document; }
}

test("IT-876 submit: no book_ids -> doFreeSearch(q, []) (Otzar's whole-library list), the user's book group is read, not changed", () => {
  const p = fakeSearchPanel({ searchInType: 6, searchInBooks: [5, 6, 7], savedListName: "משנה ברורה" });
  const r = runSubmit(p, "שבת", null);
  assert.equal(r.ok, true);
  assert.deepEqual(p.calls.map((c) => c.args), [["שבת", []]]);
  assert.deepEqual(r.ui, { type: 6, books: { count: 3 }, name: "משנה ברורה", enum: EN });
  assert.equal(p.store.searchInType, 6);
  assert.deepEqual(p.store.searchInBooks, [5, 6, 7]);
  // A saved list chosen in the combo stores an object {id, <listType>: true}, not an array.
  const p2 = fakeSearchPanel({ searchInType: 8, searchInBooks: { id: 44, free: true } });
  const r2 = runSubmit(p2, "שבת", null);
  assert.deepEqual(r2.ui.books, { list_id: 44 });
  assert.equal(r2.ui.name, null);
  assert.deepEqual(p2.store.searchInBooks, { id: 44, free: true });
});

test("IT-876 submit: book_ids -> doFreeSearch(q, ids); the selection is untouched, so nothing is left behind", () => {
  const p = fakeSearchPanel({ searchInType: 1 });
  runSubmit(p, "שבת", [11, 12]);
  assert.deepEqual(p.calls.map((c) => c.args), [["שבת", [11, 12]]]);
  assert.equal(p.store.searchInType, 1);
  assert.deepEqual(p.store.searchInBooks, []);
});

test("IT-876 submit: a leftover special/mefo search type is set to classic first, as Otzar's search box does", () => {
  for (const t of ["special", "mefo"]) {
    const p = fakeSearchPanel({ searchType: t, searchInType: 5 });
    const r = runSubmit(p, "שבת", null);
    assert.equal(r.search_type_was, t);
    assert.equal(p.calls[0].searchType, "classic", "doFreeSearch ran with the classic type");
    assert.equal(p.store.searchInType, 5);
  }
});

test("IT-876 submit: no search panel -> ok false, nothing called", () => {
  const p = fakeSearchPanel({ noComp: true });
  assert.deepEqual(runSubmit(p, "שבת", null), { ok: false });
  assert.equal(p.calls.length, 0);
});

test("IT-876 uiScopeKind: Otzar's enumSearchInType numbers, groups from custom up, mefo strings", () => {
  assert.equal(m.uiScopeKind(1, EN), "library");
  assert.equal(m.uiScopeKind(null, EN), "library");
  assert.equal(m.uiScopeKind(2, EN), "current_list");
  assert.equal(m.uiScopeKind(3, EN), "selected_books");
  assert.equal(m.uiScopeKind(4, EN), "meforesh");
  assert.equal(m.uiScopeKind(5, EN), "tsiyunim");
  assert.equal(m.uiScopeKind(6, EN), "browse");
  assert.equal(m.uiScopeKind(7, EN), "group");
  assert.equal(m.uiScopeKind(42, EN), "group");
  assert.equal(m.uiScopeKind("mefo_12", EN), "mefo");
  assert.equal(m.uiScopeKind(3, null), "selected_books", "falls back to disk 19's numbers");
});

const RES = { query: "שבת", total_results: 10, books_with_results: 4, library_books: 100, sort: "x", books: [] };
test("IT-876 scope report: every unscoped result says library; a selected group is reported as not used and left unchanged", () => {
  const plain = m.searchScopeReport(RES, null, { ok: true, ui: { type: 1, books: { count: 0 }, enum: EN } }, { type: 1, books: { count: 0 } });
  assert.deepEqual(plain.scope, { kind: "library", count: 100 });
  assert.equal(plain.otzar_selected_scope, undefined);
  assert.equal(plain.scope_warning, undefined);

  const grp = m.searchScopeReport(RES, null, { ok: true, ui: { type: 6, books: { count: 3 }, name: "תלמוד בבלי", enum: EN } }, { type: 6, books: { count: 3 } });
  assert.deepEqual(grp.scope, { kind: "library", count: 100 });
  assert.equal(grp.otzar_selected_scope.kind, "browse");
  assert.equal(grp.otzar_selected_scope.name, "תלמוד בבלי");
  assert.equal(grp.otzar_selected_scope.count, 3);
  assert.equal(grp.otzar_selected_scope.left_unchanged, true);
  assert.match(grp.otzar_selected_scope.note, /covered the whole library/);
  assert.equal(grp.scope_warning, undefined);
});

test("IT-876 scope report: book_ids -> kind books; books outside book_ids still warn; a changed selection warns", () => {
  const r = m.searchScopeReport({ ...RES, outside_scope: 2 }, [5, 6], { ok: true, ui: { type: 1, books: { count: 0 }, enum: EN } }, { type: 1, books: { count: 0 } });
  assert.deepEqual(r.scope, { kind: "books", book_ids: [5, 6], count: 2 });
  assert.equal("outside_scope" in r, false);
  assert.match(r.scope_warning, /2 book\(s\) in the results are not in book_ids/);

  const ch = m.searchScopeReport(RES, null, { ok: true, ui: { type: 3, books: { count: 2 }, enum: EN } }, { type: 1, books: { count: 0 } });
  const lst = m.searchScopeReport(RES, null, { ok: true, ui: { type: 8, books: { list_id: 44 }, enum: EN } }, { type: 8, books: { list_id: 44 } });
  assert.deepEqual([lst.otzar_selected_scope.kind, lst.otzar_selected_scope.list_id, lst.otzar_selected_scope.left_unchanged], ["group", 44, true]);
  assert.equal(ch.otzar_selected_scope.left_unchanged, false);
  assert.match(ch.scope_warning, /changed during this search/);
});

test("IT-876 doSearch never types into Otzar's search box (that would use the scope selected in the UI)", () => {
  const src = require("node:fs").readFileSync(require.resolve("../src/otzar-mcp.cjs"), "utf8");
  const body = src.slice(src.indexOf("async function doSearch("), src.indexOf("async function runSearch("));
  assert.ok(body.length > 100);
  assert.doesNotMatch(body, /Input\.insertText|dispatchKeyEvent|focus\(\)/);
  assert.match(body, /SEARCH_SUBMIT_PAGE_FN/);
});
