# Bar-Ilan Responsa for AI agents: `responsa-mcp` (IT-780)

Lets an AI agent (Claude Code, Claude Desktop, or any MCP client) search and read the user's own licensed **Bar-Ilan Responsa Project** (פרויקט השו"ת) in the background, while the user keeps working in their own Responsa. Tested live on Responsa 29; other versions are expected to work (see "Responsa versions").

## How it works

- `responsa-mcp.exe` starts **its own** `RESPONSA.exe` on a private Windows desktop that is never shown, so none of its windows ever appears on the user's screen. Responsa allows several instances, so the user's own Responsa keeps working normally.
- It drives that instance only with Win32 messages to its own process:
  - search dialog, results window, "כתיבת מקורות"
  - the search dialog's database chooser, when a search is limited to books (IT-789): a click posted to its own hidden tree window, not real mouse input
  - Responsa's own File → Save, which writes Unicode text to the tool's temp folder; the tool reads the file and deletes it (see "The Save As window" below)
- It never reads Responsa's content files (the books and their indexes) and never drives or ends the user's Responsa. It reads two small files:
  - `Responsa.env`, to find the data folder
  - the workspace autosave `DATA\responsaext1.029` (on Responsa 29; see "Responsa versions" below), which it copies, compares and may put back
- It changes no option through Responsa's Options dialog. It does switch its own instance to advanced search and may tick "search all databases"; Responsa keeps such choices in its workspace autosave, which the tool protects.
- It is a single exe with nothing to install. It is built with the C# compiler that ships with Windows (.NET Framework 4.x), as x86 to match Responsa. MCP runs over stdio only, with no network port.

## Install on a PC

1. Build once (on any Windows PC): `powershell -File responsa\build.ps1`. The result is `responsa\dist\responsa-mcp.exe`, about 90 KB.
2. Copy `responsa-mcp.exe` to a folder of your own, for example `%LOCALAPPDATA%\ResponsaMcp\bin\`.
3. Register it:
   - **Claude Code:** `claude mcp add responsa -- "%LOCALAPPDATA%\ResponsaMcp\bin\responsa-mcp.exe"`
   - **Claude Desktop** (`claude_desktop_config.json`):
     ```json
     { "mcpServers": { "responsa": { "command": "C:\\Users\\<you>\\AppData\\Local\\ResponsaMcp\\bin\\responsa-mcp.exe" } } }
     ```
4. Install the skill: copy `skill\responsa` to `%USERPROFILE%\.claude\skills\`, or upload it as a skill in Claude.
5. Check: `responsa-mcp.exe doctor` first prints the Responsa version, the workspace file it found, every Responsa install found and the environment Responsa will get (`install:`). It then starts the private instance, runs a control search, opens one text, prints how the Save As window went (`save_dialog:`) and stops. It takes about 15 seconds. When something fails on your Responsa version, send this output.

The Responsa disk (Disk-on-Key) must be connected, exactly as when the user runs Responsa.

## Tools

| Tool | Does |
|---|---|
| `status` | Starts the private instance if needed; reports install, version (`responsa_version`, `version_tested`), every install found (`responsa_installs`, the one used marked), the workspace file it protects (`workspace_file`), whether Responsa's environment had to be completed (`environment`) and open searches. |
| `search` | Advanced-syntax query. Returns the total, results per database, and the first results (source line + snippet), up to 50. Optional `books`: search only named books (see below). |
| `get_results` | Another page of a search (≤ 50). The last 3 searches are kept. Each result has its real number `n` in the search. If Responsa's save holds anything but exactly the asked results, nothing is returned: the call fails and the instance is restarted (earlier `search_id`s are gone). |
| `open_text` | One unit of text by reference (`בבלי ברכות כז ב`, `שוע אוח רסג`), up to 60,000 characters. `source` is never empty. When several sources match, `candidates` (first 20) and `opened` name them. |
| `close_windows` | Closes the result and text windows of the private instance. |

## Searching only some books (IT-789)

`search` takes an optional `books`: up to 20 names from Responsa's database tree, e.g. `["דניאל"]` or `["רש\"י ומפרשיו / רש\"י / דניאל", "מצודת דוד"]`.

- **Names.** Each item is matched against the tree's categories, sub-categories and books (the top three levels). Quotation marks and gershayim are ignored, and so is word order, because Responsa stores some names in visual order. A deeper node (for example Rashi on one book) is reached through a path, parts separated by `/`.
  - No match, or two matches at the same level: an error with the candidates (as paths). Nothing is searched.
  - One match higher in the tree than the others (e.g. `בראשית` the book and the `בראשית` parts of Hasidic books): that one is taken and `books_note` names the others.
- **Selecting.** The tool opens its instance's database chooser and presses "התחל שוב" (clear). It then posts a click to each named node of its own hidden tree. Each node is verified: its icon turns green (image 0).
  - The click is posted in the tree's own DPI awareness (IT-794). Responsa's windows are system-DPI-aware; posted from the DPI-unaware tool, Windows scaled the click point with the display scaling (at 125% a click landed one or more rows lower, or below the tree).
  - It reads the result back from Responsa's own "רשימת המאגרים שנבחרו" view. The search runs only if that view shows exactly the named nodes; otherwise the instance is restarted and an error is returned. The search never runs wider than asked.
  - The reply's `scope` is that read-back.
- **Afterwards.** The selection lives only in the tool's instance's memory. Measured on Responsa 29: confirming it writes nothing to the data folder, and the instance is never closed normally, so it is never saved. The user's own selection on disk is untouched.
  - A later search without `books` would still be limited, even with `all_databases: true` (Responsa: "החיפוש מוגבל לספרים"). So the tool restarts its instance first (about 5 s). The new instance reads the user's saved selection again. The reply has a `note`, and earlier `search_id`s are gone.
- **Speed.** The first search with `books` reads the tree names once per server (measured 6–24 s, about 10,000 nodes); later ones add about 1–5 s. Narrowing to books does not make Responsa's search itself faster.

## What it leaves behind, and what it protects

- **Search history:** the agent's searches go into Responsa's former-queries list (`DATA\responsahst.029`), and opened texts into the former-texts list (`responsahis.029`). These lists are shared with the user's Responsa; that is how Responsa works.
- **Workspace autosave (`DATA\responsaext1.029` on Responsa 29).** Responsa writes this file every few minutes and when it exits normally, and reopens those windows on the next start. The tool's instance writes it too. The tool keeps the user's version safe as follows:
  - **Before start:** the tool keeps a copy of the user's version, one copy per server and per instance (`%LOCALAPPDATA%\ResponsaMcp\workspace\`). A crash, a restart or another server never overwrites it.
  - **A second server** (another MCP client) never copies the file as it is on disk while another server's instance is alive, because that instance may have just written it. It takes that server's copy instead, so all servers put back the same bytes. If two live servers keep different copies, a third does not start its instance and says so.
  - **While its instance is alive:** the tool checks the file every 1.5 s and whether the user's Responsa is running.
    - **The user's Responsa is not running:** a change can only come from a tool instance, so the user's version is put back at once. If the file did not exist before, it is removed. If another live server keeps a different copy, nothing is written.
    - **The user's Responsa is open:** a change in that time cannot be attributed. The tool leaves it, and the user's own later autosave or exit save decides. The tool remembers the last bytes it saw in that time.
    - **The user's Responsa exits:** Responsa writes its exit save when it is asked to close, and the process ends 2 to 6 seconds later (measured on Responsa 29). If the file was last written no later than the moment the user's Responsa exited, it is that exit save, and it becomes the version the tool protects. If it was written after that moment, the tool's instance wrote it: the last bytes seen while the user's Responsa was running become the protected version and are put back.
    - **The user's Responsa crashes or is ended** (exit code not 0, so there is no exit save): the file may hold the tool's autosave from the overlap, or the user's own last autosave, and the tool cannot tell which. It writes nothing. If the file differs from the protected version, that version is kept aside as `*.kept.bak` (listed in `status`), and the file as it is becomes the version protected from then on.
  - **If the tool's instance crashes:** the tool settles the file first, then keeps a new copy and starts a new instance.
  - **When the tool's instance ends,** and **at the next server start for a server that died:** the copy is removed only if the workspace file equals it. Otherwise the tool writes nothing to the workspace file. It keeps the copy intact as `*.kept.bak` (with `*.kept.meta`) and lists it under `kept_workspace_backups` in `status`, so it can be restored by hand.
    - This happens, for example, when the server is killed after the tool's instance autosaved while the user's Responsa was open, or when the instance ends while the user's Responsa is open and the file changed.
    - Kept copies are never deleted by the tool. They are small (a few KB to a few hundred KB) and can be deleted by hand once the user's Responsa shows the right windows.
  - **To restore a kept copy by hand:** close every Responsa, then copy the `*.kept.bak` file over the workspace file named in `status` (`DATA\responsaext1.029` on Responsa 29). The `file=` line of the `*.kept.meta` next to it names the file it was kept from.
  - **Residual case:** after the user's Responsa crashes during an overlap in which the tool's instance autosaved, the next Responsa the user opens may show the tool's windows. The user's previous layout is not lost: it is the kept copy listed in `status`.
  - **At start:** the tool closes any windows its instance restored from the file.
- **Ending the instance:** the tool terminates its own process through the handle it got when starting it, so its PID is never confused with a reused one. There is no Responsa exit routine, so Responsa's exit save of its settings never overwrites the user's. The instance record and the copy are released only after the process has actually exited. If it does not exit within 15 s, everything is kept and no new instance is started. At start-up Responsa itself adds a `Report=` key to `Responsa.ini`.
- **When the instance ends:** when the MCP client closes the server (stdin closes). An instance left by a server that crashed is ended the next time a server starts. Only instances the tool recorded are ended, after checking that the same process is still `RESPONSA.exe` with the recorded creation time. If that check cannot be read (the name or creation time of a running process), nothing is ended and the record is kept for the next start.
- **Errors:**
  - If an operation gets stuck (a search or window that does not finish in time), or Responsa shows an internal error or an unknown window, the tool restarts its instance at once and says so. Nothing late from that operation can leak into the next call.
  - A stored results window is used only while it is still a window of the tool's own instance.
- **Temp files:** extracted text is deleted in the same call, even when saving fails. Each server has its own temp folder, removed at shutdown; folders of dead servers are removed at the next start.

## Limits

- Per call: at most 50 results and 60,000 characters of text. Extraction is meant to be small and per question.
- Responsa refuses searches with more than 32,000 results.
- One search at a time per server. Each MCP client process gets its own private instance.
- Tested live on Responsa 29.0 (ResponsaCD29C, "29 ragil DISKLESS") on Windows 11. Other versions: see below.

## Responsa versions

- **Tested live:** Responsa 29 only. On that version every data file ends in `.029`, so the suffix is taken to be the version number. That is an inference, not checked on another version.
- **Other versions are expected to work, and are not refused.** `status` and `doctor` show `version_tested: false` and a note. Pilot users reported Responsa 31 and 34; the Save As handling below was changed for what they saw (IT-827).
- **Report problems with the output of `responsa-mcp.exe doctor`.** Where a window of another version differs from Responsa 29 (a control the tool measured on 29 is missing, the Save As window is not recognized, or Responsa saves elsewhere), the tool returns an error that names the version and the step. It never returns a raw exception or text from another file.
- **Several versions installed:** the tool collects every install it finds (the uninstall registry and `Program Files\Responsa*`) and uses the newest by file version. Its data folder (`Responsa.env`) and workspace file are those of that install. `status` and `doctor` list every install found under `responsa_installs`, with the one used marked `used: true`.
  - `RESPONSA_MCP_EXE` still overrides the choice. If it is set to a path that does not exist, the tool says so instead of falling back to another install.
- **Distance order (IT-868):** Responsa 29 and 34 read `[a:b]` as min:max; Responsa 31 (measured by a pilot) as max:min and rejects `[1:3]` with "המרחק המקסימלי קטן מהמרחק המינימלי". On that message `search` swaps the two numbers of every `[a:b]` and runs the query once more, and says so in `ran_as` and `distance_note`. `doctor`'s control search goes through the same retry.

### The environment Responsa is started with (IT-868)

Responsa needs the `TMP` variable and does not fall back to `TEMP`. MCP clients start servers with a reduced environment; Claude Desktop passes no `TMP`. Measured on Responsa 29: with only that environment a search fails with "שגיאה מס' 15: לא ניתן לפתוח את הקובץ" and "שגיאה מס' 9: בעיית קלט/פלט"; a Responsa 31 pilot got error 15 already at start-up. From cmd everything works, because the full environment is there.

- The tool starts its `RESPONSA.exe` with its own environment, plus every variable of the user's default environment (`CreateEnvironmentBlock`, what a new logon of this user gets) that the client did not pass. If `TMP` is still missing or empty, it is set from `TEMP` or the system temp path.
- A variable the client set is never removed or changed (`PATH`, `RESPONSA_MCP_EXE`, ...). Nothing of the user's own environment or registry is written.
- `status` and `doctor` show `environment`: "complete", or that `TMP` was missing and how many variables were added. Only well-known Windows names are listed; the user's own variables are counted, never named.
- If Responsa still reports error 15, the result carries a `hint` with the `TMP` it got and what to check, and `doctor` ends with `ERROR`.

### The Save As window (IT-827)

The tool gets text out through Responsa's own File → Save, so it must fill Responsa's Save As window. Windows has two kinds:

- **The common item dialog** (folder tree, address bar). Responsa 29 on Windows 11 shows this one, as does Responsa 34 (pilot report).
- **The classic dialog**, which older programs or other settings may show.

The tool recognizes both by their parts: the file-name field (the one visible edit inside a combo box outside the address bar), the type box and the Save button. Then it does the following:

1. It waits until the window has finished putting in its default name.
2. If the type box has a "UNICODE" entry and another type is chosen, it chooses the Unicode one, the way a pick from the list would.
3. It types the tool's path into the file-name field and checks that the field still holds exactly that path a moment later.
4. It presses Save. The save counts only if the file appears at the tool's own path. A file Responsa wrote elsewhere (for example `respsrch` in Responsa's own `TEMP` folder) is never read.
5. If Responsa saved elsewhere, the save is repeated once, with the path put in by `WM_SETTEXT` instead of typed. If that also fails, the error names the version and says what the window did (also printed by `doctor` as `save_dialog`).

Why typing: a pilot user's Responsa 34 saved to its default name `respsrch` even though the tool had put its path in the field. A scratch program on this PC shows the same with the item dialog: after its type changes, the dialog resets the name to its default. It then saves under that internal name, even while the field shows a path put in by `WM_SETTEXT`. A typed path is taken. Measured on Responsa 29: both ways work, and 16 saves in a row went through on the typed first round. Once in a live run, the typed round did not reach the tool's path and the second round saved correctly; the cause was not found.

`build.ps1 -DialogTest` drives this code against a scratch program's Save As windows on a private desktop: both kinds, a type that starts on the non-Unicode entry, a name reset after the window is shown, a program that saves elsewhere, and a window that never keeps the path. No Responsa is needed.
- **Which workspace file is protected:** the tool looks in `DATA\` for `responsaext1.0NN` (three digits, the first `0`; a longer name such as `responsaext1.029_` does not count). It decides once, each time its instance starts:
  - **One such file:** that file is protected, as described above.
  - **None:** the name this version is expected to create (`responsaext1.030` for Responsa 30; `.029` if the version cannot be read) is protected as a file that did not exist before, so a file only the tool's instance creates there is removed.
  - **Several** (for example `.029` and `.030` left by an upgrade): the tool does not guess. It does not start its instance and returns an error naming the files. The user can move the file of a version that is no longer installed out of `DATA\`, with every Responsa closed.
  - **Two servers that found different files** (one started before the file appeared): the second does not start its instance until the first has stopped.
- **To see what the tool uses:** `responsa-mcp.exe doctor` prints `responsa_version`, `version_tested` and `workspace_file` before it starts anything; `status` shows the same.

## Development

- `src/`: the server.
  - `Program.cs`: entry point (`serve`, `doctor`, `version`)
  - `ToolDefs.cs`: tool definitions and input schemas
  - `Books.cs`: the books scope (IT-789): name matching, the database tree reader
  - `Mcp.cs`: JSON-RPC and argument validation
  - `Tools.cs`: tools and caps
  - `Instance.cs`: install discovery, the workspace file (`WorkspaceFile`, IT-785), lifecycle, orphans
  - `WorkspaceGuard.cs`: workspace protection
  - `Proc.cs`: process identity (handle + creation time)
  - `Ui.cs`: Responsa UI operations and the measured command/control IDs
  - `SaveDialog.cs`: recognizing and filling the Save As window, both kinds (IT-827)
  - `UiThread.cs`: the private desktop and the error types
  - `Native.cs`: Win32
  - `SaveParse.cs`: parsing the saved files
- `build.ps1 -Test`: builds and runs the offline tests (`test/OfflineTests.cs`, synthetic samples in `test/samples`).
- `build.ps1 -DialogTest`: builds `test/SaveAsHost.cs` (a scratch program with a Save As window, never shipped) and `test/SaveDialogTest.cs`, and drives the Save As code against it on a private desktop. It needs no Responsa, shows nothing on screen and takes about a minute.
- `test/live-mcp.ps1`: end-to-end test through MCP. It needs Responsa. Run it with PowerShell 7 (`pwsh`): the file is UTF-8 without a BOM.
- Design notes and the measurements behind the IDs are in `docs/responsa-mcp-plan.md`. That file is local and not committed.
