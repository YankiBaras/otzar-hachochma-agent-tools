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
const reset = () => { m.state.port = null; m.state.install = null; m.state.launchedByUs = false; };

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
  await assert.rejects(m.ensureOtzar(), (e) => e instanceof m.UserError && /already running without/.test(e.message));
  assert.equal(spawned, false);
});

test("a port on the command line that is not Otzar is never used", async () => {
  reset();
  const chrome = await fakeCdp("Mozilla/5.0 Chrome/120.0");
  try {
    m.deps.otzarProcesses = () => [{ pid: 1, exe: "X:\\otzar.exe", cmd: `otzar.exe --remote-debugging-port=${chrome.address().port}` }];
    await assert.rejects(m.ensureOtzar(), /already running without/);
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
