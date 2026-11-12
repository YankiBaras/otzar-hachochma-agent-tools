---
name: otzar-hachochma
description: Search and read books in the user's own licensed Otzar HaChochma (אוצר החכמה) library on this PC, through the otzar-hachochma MCP tools (status, search, search_hits, get_hit_context, get_page_text, get_page_image, book_info, find_book, list_references, go_to_reference). Use when the user asks to find a source, a quote, a phrase or a topic in Torah literature, to check where a word appears, or to read a page of a sefer in Otzar HaChochma.
---

# Otzar HaChochma (אוצר החכמה)

Otzar HaChochma is a licensed desktop library of scanned Torah books. The `otzar-hachochma` MCP server reads it in the background while the user keeps working. The tool itself creates, changes or deletes no file in the Otzar installation or on the Otzar data drive, and it never closes Otzar. One thing does get written, by Otzar itself: every `search` (including the control search of the coverage check) is an ordinary Otzar search, so Otzar saves it in its own search history, in its database (on the tested PC, `data.db` in the Otzar program folder), exactly as when the user searches by hand.

## Start of a session

1. Call `status` once. It connects to Otzar and starts it if it is closed.
   - If it says Otzar is **already running without the agent connection**, ask the user to close Otzar themselves (after saving their work) and open it again from the **"אוצר החכמה ל-AI"** desktop shortcut (made once with `otzar-mcp.exe shortcut`), then call `status` again. Never try to close it yourself.
   - If the user closes Otzar during the work, the next call opens it again. Do not ask them to restart Claude.
   - If it says **the Otzar HaChochma data disk is not connected**, tell the user to connect the Otzar disk, then try again. Do not retry before that.
   - If it says **Otzar HaChochma is stuck** (opened but not answering), relay the message: check that the disk is connected, close Otzar if it shows a message, and restart the PC if Otzar cannot be closed. Do not retry in a loop.
   - Read every `warnings` entry and tell the user about it in one line. A version warning means a newer Otzar than the one the tool was tested with; tools may still work.
2. Once per working day, or when results look too few, call `status` with `coverage_check: true`. It runs a control search (`שבת`).
   - If `coverage.ok` is false, tell the user that Otzar's search covers too few books. They should check the search settings in Otzar (a book list or filter may be selected, or a setting is stuck) or install the latest Otzar update. Do not tell them to run Otzar as administrator; that is not the fix.
   - Until it is fixed, say clearly that search results are incomplete.

## Workflow

When the question is about a known book or set, the fastest order is: `find_book` → `search` with `book_ids` (its volumes) → `get_hit_context` → `get_page_text` or `get_page_image` to verify. For a question across the whole library:

1. `search` with a query in Otzar syntax (below). The result has `total_results`, `books_with_results`, `library_books` and the top books by relevance, each with `book_id` and its number of `results`.
2. `search_hits` with a `book_id` from those results: the pages where the query matched (`page` = position in the book, `page_label` = the printed page or daf), with snippets. The hit words are marked «like this».
   - To search inside one book or one set only, pass `book_ids` to `search` (up to 200 ids, e.g. the `volume_ids` from `find_book`). It is faster and keeps the results to that book. It is still visible in Otzar's search panel, like any search.
   - `get_hit_context` with a `book_id` from the results gives each hit with only the words around it (`window`, default 15). Use it to judge hits before reading whole pages; page through with `offset`.
3. `get_page_text` for the full OCR text of a page, and `get_page_image` to see the page itself. Look at the image whenever the exact wording matters: OCR has mistakes, misses vocalization and abbreviations, and mixes columns.
4. `book_info` for the bibliographic details to cite: name, volume, author, place and year.

## Finding a specific book (checking a citation)

When the user gives a citation (book, volume, siman or page), do not search the topic words across the whole library. Go to the place directly:

1. `find_book` with the book's name, e.g. `אגרות משה`. It uses Otzar's own book-name search, in memory: it changes nothing on the user's screen. The main volume of a set lists every volume in `volume_ids`. If you write `שו"ת …` and it finds almost nothing, the tool retries without `שו"ת` and says so.
   - Two spaces between the name and the reference (`משנה ברורה  סימן א`) return `reference_part`, ready for `go_to_reference`.
   - Column search: `|ספר:משנה ברורה&מחבר:כהן|` (also `מקום:` and `שנה:` with a range).
2. `go_to_reference` with the `book_id` and the reference: `סימן קכג`, `הלכות תשובה ג א`, `ב א` or `דף יג.` for Shas. It reads Otzar's own index (ציונים), so it finds the page where a siman starts even when the scan does not show the number. It returns `page`, `page_label`, `match` (how sure: `exact`, `suffix`, `whole_words`, `substring`) and `next_reference` (where the next one starts, so you know where this one ends). Check the `matched_title` before you rely on a `substring` match.
3. `get_page_text` or `get_page_image` with that `page`.
4. If `go_to_reference` or `list_references` says it **could not read the index** of the book, they do not work on this Otzar version yet. Use `get_page_text` with `page_label`, or `search` with `book_ids`, and ask the user to send the message (or the output of `otzar-mcp.exe doctor --book <id>`) to the developer.
   If `go_to_reference` finds nothing, call `list_references` with a short `filter` (one word of the reference) to see how this book names its sections. Many books have no index at all; then use `page_label`.
5. Keep a short list of the book ids you found (name, volume, `book_id`) and reuse it for the rest of the task.

If a volume still does not appear, it may be listed under another title or spelling, or it may be outside this user's license. Ask the user to look it up in Otzar and give you its book number.

## Saving tokens

- Search with **two or three distinctive words**, not a whole sentence. Without symbols, every word must appear in exactly the typed form and near the others. One OCR error or one different word gives zero results. If a long query returns 0, drop words before deciding the text is not there.
- Judge hits with `get_hit_context` (about 50 tokens per hit) or the snippets of `search_hits`. Read a page with `get_page_text` only when that is not enough, and pass `max_words` to read only the start of a long page.
- Use `search_hits` with `snippets: false` and a small `max_pages` when you only need to know on which pages a book matches.
- Ask `search` for a small `limit` (5 to 10 books) unless the user needs the full list.
- In responsa and other books cited by siman, the scanned text often does not show the siman number. Use `go_to_reference` to find the page where the siman starts, and say which page label you verified.

`search_hits` works on the **current** search. If the user or another call ran a different search in the meantime, run `search` again.

The search is typed into Otzar's own search panel, so the user sees each search there, it replaces what they had there, and Otzar adds it to its search history. Tell the user this before running many searches while they work in Otzar. A search limited with `book_ids` appears in Otzar's search history without its text (an Otzar quirk); this is harmless. If Otzar is busy with another search, the tool waits for it to finish first. If the user starts another search while the tool's search runs, the tool reports an error instead of returning the wrong results: just run it again.

## Citing

Every quote or claim from Otzar needs: book name, volume if any, author, and the page label (`page_label`), or the position (`page`) if there is no label. Quote from the page text. Check the image when the text looks garbled. Never invent a quote that you did not read through the tools.

## Search syntax (Otzar HaChochma help guide)

Separate words with spaces. Without symbols, Otzar finds each word in exactly the form typed (no prefix letters), with the words near each other.

| Symbol | Meaning | Example |
|---|---|---|
| `+` | Also with prefix letters (אותיות שימוש: ו, ה, ב, ל, מ, ש, כ…) | `+שבת` |
| `-` | Between two words: the words right next to each other | `מעשה-בראשית` |
| `&` | The words in this order | `מעשה & בראשית` |
| `{N}` | At most N words between the two words. Write it attached to the words, without spaces | `מעשה{3}בראשית` |
| `--` | Exclude: results without this word | `שבת --חול` |
| `#` | Also OCR look-alike letters (ד/ר, ט/מ…) | `#ראשית` |
| `@` | Both full and defective spelling (כתיב מלא/חסר) | `@שולחן` |
| `*` | Any number of letters | `ברא*` |
| `?` | Exactly one letter | `ב?ר` |
| `!` | Up to the maximum number of letters | `ברא!` |

Tips:

- Start broad, then narrow down with `-`, `&`, `{N}` and `--`. A symbol written the wrong way (for example `{3}` between spaces, or `-` before a single word) can give zero results; if a query with symbols returns 0, try it without them.
- Search **without quotation marks** (`"` or gershayim) first: a query with them often returns 0. For an abbreviation (ראשי תיבות), try it without the mark, then the spelled-out words.
- When a word repeats on a page, `get_hit_context` can return several almost identical windows. Read one of them and skip the rest.
- Old books have more OCR errors. Use `#`, or search a rarer word from the same passage.
- Zero results do not prove that a text does not exist. OCR may have missed it, or the book may not be in this user's license.

## Licenses differ between users

Each user's Otzar license includes different book packages. A book, or a search result, that one user has may be missing for another. If a known book is missing, `book_info` says it is not in this library, or its image is a small "error" placeholder. Say that it may be outside the user's license. Do not say that the book does not exist.

## What not to do

- Do not close, restart or reconfigure Otzar, and do not ask the tool to.
- Do not try to reach Otzar in other ways (screenshots, mouse clicks, other ports). Use these tools only.
- Do not copy whole books. Read the pages the user's question needs.
