# Otzar HaChochma for AI agents: portable package (IT-742)

Lets an AI agent (Claude Desktop, Claude Code, or any MCP client) search and read the user's own licensed Otzar HaChochma library in the background, while the user keeps working. The package itself creates, changes or deletes no file in the Otzar installation or on its data drive, and it never closes Otzar. Searches are the exception, written by Otzar itself: `search` and the coverage check are ordinary Otzar searches, so Otzar saves them in its own search history (on the tested PC, `data.db` in the Otzar program folder), and they replace the search shown on screen.

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

## How it connects to Otzar

- It finds Otzar through the `OTZARAPPCD` variable set by the installer. If that fails, it tries desktop or Start-menu shortcuts to `otzar.exe`, then the standard `\OtzarApp\OtzarLocal\launcher\bin\x64|x86\app` folder on each local fixed disk (network, removable and optical drives are skipped).
- If Otzar is closed, the first tool call starts it with a free debugging port on 127.0.0.1. Run `otzar-mcp.exe launch` to do the same by hand.
  - It starts Otzar at most once per server run, and only from a normal user environment.
  - Set `OTZAR_MCP_NO_LAUNCH=1` to turn automatic start off.
- If Otzar is already open without the debugging port, the tool asks the user to close it and try again. It never closes Otzar itself.
- `otzar-mcp.exe doctor [--coverage]` prints the connection, versions and capabilities. `--coverage` adds the coverage check.

## Tools

`status`, `search`, `search_hits`, `get_page_text`, `get_page_image`, `book_info`. See the skill for how to use them.

## Notes

- `search` types into Otzar's own search panel: the user sees the agent's searches there, each one replaces the search on screen, and Otzar adds each one to its search history. A search started while Otzar is busy waits for the running search to finish; a search replaced by another one returns an error, never the other search's results.
- License packages differ between users, so a book that one user has may be missing for another.
- While Otzar runs with the debugging port, any program on this PC can control it. The port listens on 127.0.0.1 only. The MCP server itself opens no port at all (stdio only).
- The exe is unsigned: the signature of the node.exe it is built from is removed during the build. Windows SmartScreen may warn the first time it runs.
- Network stations are not supported (untested).
