# Otzar HaChochma for AI agents: portable package (IT-742)

Lets an AI agent (Claude Desktop, Claude Code, or any MCP client) search and read the user's own licensed Otzar HaChochma library in the background, while the user keeps working. The package itself creates, changes or deletes no file in the Otzar installation or on its data drive, and it never closes Otzar. The only file it ever writes is its own desktop shortcut, and only when you run `otzar-mcp.exe shortcut`. Searches are the exception, written by Otzar itself: `search` and the coverage check are ordinary Otzar searches, so Otzar saves them in its own search history (on the tested PC, `data.db` in the Otzar program folder), and they replace the search shown on screen.

## What's inside

| Path | What |
|---|---|
| `dist\otzar-mcp.exe` | The MCP server, one file. Nothing else to install: no Node, no admin rights. |
| `skill\otzar-hachochma\SKILL.md` | The skill: workflow, Otzar search syntax, citing rules, license notes. |
| `src\otzar-mcp.cjs` | Source of the server. |
| `build.ps1` | Builds the exe on a machine that has Node 22+ (no download). |
| `test\offline.test.cjs` | Offline tests (no Otzar needed): `node --test package\test\offline.test.cjs`. |

## Install on a PC

1. Copy `otzar-mcp.exe` to a folder of your own, for example `%LOCALAPPDATA%\OtzarMcp\`. Do not put it inside the Otzar folders.
2. Register it as an MCP server:
   - **Claude Code:** `claude mcp add otzar-hachochma -- "%LOCALAPPDATA%\OtzarMcp\otzar-mcp.exe"`
   - **Claude Desktop:** in `claude_desktop_config.json`:
     ```json
     { "mcpServers": { "otzar-hachochma": { "command": "C:\\Users\\<you>\\AppData\\Local\\OtzarMcp\\otzar-mcp.exe" } } }
     ```
3. Install the skill: copy the `otzar-hachochma` folder to `%USERPROFILE%\.claude\skills\`, or upload it as a skill in Claude.
4. Recommended: make the desktop shortcut once, from the folder of step 1:
   ```
   "%LOCALAPPDATA%\OtzarMcp\otzar-mcp.exe" shortcut
   ```
   It adds **"אוצר החכמה ל-AI"** to the desktop. Open Otzar from this shortcut instead of the usual one, and the agent can always connect to it.

## The "אוצר החכמה ל-AI" shortcut

- Otzar can accept the agent connection (a debugging port) only if it was opened with one. The usual Otzar shortcut opens it without, so the agent cannot connect to an Otzar opened that way.
- The new shortcut runs `otzar-mcp.exe launch --shortcut`:
  - Otzar not running: it opens Otzar with a free debugging port picked at that moment, so a busy port is never a problem.
  - Otzar already running with the connection: it only brings Otzar to the front.
  - Otzar already running without the connection: a message asks you to close Otzar (save first) and open it again from this shortcut. Nothing is closed.
- It is a normal shortcut on your desktop, with Otzar's icon. Otzar's own shortcut and files are not changed. Running `shortcut` again updates it (for example after moving `otzar-mcp.exe`). A file with the same name that is not this tool's shortcut is left alone.
- The shortcut points to `otzar-mcp.exe`. If you move or delete that file, run `shortcut` again from the new place.

## How it connects to Otzar

- It finds Otzar through the `OTZARAPPCD` variable set by the installer. If that fails, it tries desktop or Start-menu shortcuts to `otzar.exe`, then the standard `\OtzarApp\OtzarLocal\launcher\bin\x64|x86\app` folder on each local fixed disk (network, removable and optical drives are skipped).
- If Otzar is closed, the next tool call starts it with a free debugging port on 127.0.0.1. Run `otzar-mcp.exe launch` to do the same by hand.
  - **Closing Otzar is fine.** If you close Otzar, the next request opens it again. There is no need to restart Claude.
  - Protection against a start loop: at most 2 automatic starts in 10 minutes. If a start never connects (Otzar shows an error, for example), the tool does not start Otzar again by itself until you open Otzar from the shortcut. In both cases the agent gets a message telling you what to do.
  - Otzar is started only from a normal user environment.
  - Set `OTZAR_MCP_NO_LAUNCH=1` to turn automatic start off.
  - **The Otzar data disk must be connected.** Before it starts Otzar (automatically, `launch`, or the shortcut), the tool checks for the disk with Otzar's own rule: a drive that has `data\otzar_ver.txt` or `otzarDisk\data\otzar_ver.txt`. Only these two files are checked, read-only. If no drive has them, Otzar is not started, and you get a message (Hebrew in the shortcut's box) to connect the disk and try again. In network mode (`onet.otzar` in the `otzarLocal` folder) no disk is needed, and nothing is checked. Like Otzar's own starter, each drive gets its own time limit: all drives are checked at the same time, with 5 seconds each. A drive that does not answer in time (for example a dead network drive) is skipped, and the others are still checked. The disk is found as soon as one drive has the marker, without waiting for a drive that hangs. If every drive answered or was skipped and none has the marker, Otzar is not started. Only a check that cannot run at all does not block: the install folder is not the standard `otzarLocal` layout, the drive list cannot be read, or a drive check cannot be started. Set `OTZAR_MCP_SKIP_DISK_CHECK=1` to turn the check off.
- If Otzar is already open without the debugging port, the tool asks you to close it and try again, or to open it from the shortcut. It never closes Otzar itself.
- An Otzar opened with the debugging port that does not answer yet is treated as still loading, but only for a limited time: the launch wait (120 s by default) plus one minute after it was started. After that the tool says Otzar is stuck and does not answer. It asks you to check that the Otzar disk is connected, to close Otzar if it shows a message, and to restart the PC if Otzar cannot be closed. It does not wait for it any more. If the disk is missing at that moment, the message says so.
- `otzar-mcp.exe doctor [--coverage] [--book <id>]` prints the connection, versions, capabilities and the disk check. `--coverage` adds the coverage check. `--book <id>` checks that the tool can read that book's index (ציונים): which decoder read it, the body length, its first 16 bytes and the number of references. It never prints the book's text. Run it, and send the output, if `go_to_reference` or `list_references` fails.

## Tools

`status`, `search`, `search_hits`, `get_hit_context`, `get_page_text`, `get_page_image`, `book_info`, `find_book`, `list_references`, `go_to_reference`. See the skill for how to use them.

| Tool | What it does | Changes Otzar's window? |
|---|---|---|
| `find_book` | Finds books by name with Otzar's own book-name search (F2), in memory. Supports `name  reference` (two spaces) and `\|ספר:..&מחבר:..&מקום:..&שנה:..\|`. A leading `שו"ת` / `שות` / `ספר` that finds fewer than 3 books is dropped and the search repeated (the result says so). Returns `volume_ids` for a multi-volume set. | No (no search, no history) |
| `search` with `book_ids` | Full-text search only in the given books (1 to 200), through Otzar's own search method. | Yes, like any `search` |
| `get_hit_context` | Each hit of the current search in one book, with only N words around it (default 15). Hit words in «», a page change marked ‖. Pages with `offset`. | No |
| `list_references` | The book's index (ציונים): siman, daf/amud, perek/halacha, with the page each starts on. | No |
| `go_to_reference` | The page where a siman/daf/halacha starts, from that index, and where the next one starts. | No |

Example of checking a citation: `find_book "אגרות משה"` → `go_to_reference 107416 "סימן קכג"` → `get_page_text 107416 page 204`.

## Notes

- `search` types into Otzar's own search panel: the user sees the agent's searches there, each one replaces the search on screen, and Otzar adds each one to its search history.
  - A search with `book_ids` is limited to those books, but Otzar saves it in its search history as an empty line: the entry has no search text and no book list (tested live). The results are still correct. The tool does not touch Otzar's scope combo.
  - In Otzar's "special" search type, a book list is not supported, so a search with `book_ids` is refused with a clear message.
- `find_book`, `get_hit_context`, `list_references` and `go_to_reference` only read. The index tools read Otzar's book index (`api/books/data/<id>`), the same data the reader loads when a book is opened. The index is decoded with Otzar's own decompression code, the same steps as Otzar's reader, so it works whatever the disk version stores (it was tested on disk version 19). If that code is not found, the tool's own decoders try zlib, gzip, raw deflate and plain JSON. An index it still cannot read gives a clear error with the details to send to the developer, never a raw `SyntaxError`.
- Two starts close together (two clicks on the shortcut, or the shortcut and an agent) are serialized by a lock of this user (a named pipe that accepts no connection, not a network port). The second one waits, then connects to the Otzar the first one opened, and does not start another one. An Otzar that is still loading is waited for instead of being reported as "open without the connection".
- The shortcut's message box stays until it is closed (it used to close itself after 30 seconds). A search started while Otzar is busy waits for the running search to finish; a search replaced by another one returns an error, never the other search's results.
- License packages differ between users, so a book that one user has may be missing for another.
- While Otzar runs with the debugging port, any program on this PC can control it. The port listens on 127.0.0.1 only. The MCP server itself opens no network port at all (stdio only; the start lock is a local named pipe that refuses every connection).
- The exe is unsigned: the signature of the node.exe it is built from is removed during the build. Windows SmartScreen may warn the first time it runs.
- Network stations are not supported (untested).
