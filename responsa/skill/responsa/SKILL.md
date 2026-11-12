---
name: responsa
description: Search and read the Bar-Ilan Responsa Project (פרויקט השו"ת, שו"ת בר אילן) v29 installed on this PC, through the responsa MCP tools (search, get_results, open_text). Use for finding Torah sources, quotes, halachic discussions in responsa, Talmud, Rambam, Shulchan Aruch, commentaries, and for reading a specific source by reference.
---

# Bar-Ilan Responsa (שו"ת בר אילן) through the `responsa` MCP tools

The tools drive the user's own licensed Responsa program in a private, invisible instance. The user's own Responsa window is never touched. Searches are real Responsa searches: they also appear in Responsa's list of former queries.

## Workflow

1. `search` with an advanced-syntax query. Pass `all_databases: true` unless the user asked for specific books.
2. Look at `total` and `by_database`.
   - Too many results (Responsa refuses more than 32,000): narrow the query.
   - Zero results: broaden the query (prefixes, spellings, abbreviations, synonyms).
3. Read the snippets. Use `get_results` for more (at most 50 per call).
4. To read a result in full, call `open_text` with a reference built from its source line (see "References" below). Each call returns one unit: an amud, a chapter, a siman or a teshuva.
5. **Cite exactly the source line Responsa returned** (`source`). Never invent a reference. Quote the text as returned; do not "fix" it.

Keep extraction small and to the point of the question. Don't page through whole result sets or read whole books.

## Query language (advanced search)

Type the query in logical order, the way it is read. Rows marked **(checked)** were tested live on Responsa 29. The other rows come from Responsa's own help and guide.

| Want | Write | Note |
|---|---|---|
| exact word / phrase | `נר שבת` | words separated by single spaces must be adjacent and in order (single words checked) |
| grammatical prefixes (ו ה ב כ ל מ ש...) | `#שבת` | almost always add it (checked) |
| grammatical suffixes | `שבת#` | both: `#שבת#` |
| any letters before / after | `*בית` / `בית*` | noisy (חבית, ערבית) |
| plene/defective spelling | `!יהושוע` | write the fullest spelling; with prefixes `#!ירושלים` |
| with/without gershayim | `+רמב"ם` | |
| root / lemma | `%קדש` / `$מדרגה` | root ⊇ lemma; write a lemma in כתיב חסר; root (checked) |
| Aramaic equivalent | `^עולה` | |
| alternatives | `(משה/אהרן)` | slash, inside parentheses; operators inside or outside: `#(בית/אפטרופ)*` (checked) |
| multi-word alternative | `({{צער בעלי חיים}}/{{צעב"ח}})` | without `{{}}` you get "מילה יכולה להכיל אותיות עבריות וספרות בלבד" (the error is checked) |
| exclude (one component per query) | `יום -(כיפור/הכיפורים)` | (checked, with `[-5:5]`) |
| distance: 2nd word 1..3 words after 1st | `נר [1:3] שבת` | **min:max**. `[3:1]` is an error (checked) |
| either order | `#בה"מ [-5:5] #שבת` | negative min allows the 2nd word before the 1st (checked) |
| every adjacent pair within N, any order | `5: %קדש #שם` | **number then colon**; `:5 ...` fails (checked) |
| same sentence / paragraph / document | `משפט: $מנהג $שבת #ביהכ"נ` | also `פסקה:` and `מסמך:` (`משפט:` checked) |
| wildcards inside a word | `*` any letters, `?` one letter, `~` one or none | `דנ~אל`, `מ?ה` |
| optional / alternative letter | `אירו(ס/ש)ין`, `ה(ע/@)לר` | `@` = no letter |
| saved search / word family | `{name}` / `<name>` | only if the user has them |

What the units mean, by database (doc): Tanach משפט = verse, פסקה/מסמך = chapter. Rambam: פסקה = halacha, מסמך = chapter. Shulchan Aruch: פסקה = se'if, מסמך = siman. Responsa: פסקה = section, מסמך = siman. In the Bavli, מסמך is a whole chapter.

Tips:
- Text in parentheses or slashes inside the source counts as words. Allow a small distance even for "adjacent" words: `אומר [1:3] עשה`.
- Search abbreviations and synonyms too: `(#ריב"ז/#רבן יוחנן בן זכאי)` won't work (multi-word without `{{}}`); run two searches or use `{{...}}`.
- Hebrew/Aramaic endings (ה/א, ם/ן) have no symbol: use alternatives, e.g. `(מדליקים/מדליקין)`.
- God's name is stored as `יקוק`.
- Responsa echoes queries mirrored (`נר ]1:3[ שבת`); that is display only.

Errors come back in `responsa_messages`, verbatim in Hebrew:
- "שגיאה בהגדרת השאילתה: ..." means the syntax is wrong. Fix it and search again.
- "נמצאו מעל 32000 תוצאות" means the query is too broad.
- "לא נמצאה כל תוצאה!" means no results.

## References for `open_text`

Write references the way Responsa's "כתיבת מקורות" accepts them:
- no commas, and no ע"א / ע"ב
- quotation marks optional

| Source | Write |
|---|---|
| Bavli | `בבלי ברכות כז ב` (also `גמ שבת כ ב`; amud ב: `בבלי שבת לג:`) |
| Rashi / Tosafot | `רשי ברכות ב א`, `תוס במ ה ב` |
| Mishnah | `משנה ברכות א א` |
| Rambam | `רמבם תפילה ד א`, `רמבם מאכלות` |
| Shulchan Aruch | `שוע אוח רסג`, `שוע יוד כ א` |
| Mishnah Berurah | `מב תרמב` |
| Responsa | `יביע א אוח לד`, `אגמ יוד א ג`, `נובי קמא אוח לב` |

Rules:
- Never write `פ"א` for chapter 1: it is read as 81. Write `א` or `פרק א`.
- Ambiguous references open Responsa's first choice. The result then has a `note`, and `source` says what was actually opened. Make the reference more specific (`בבלי`, `מסכת`, `דף`) if it is not what you wanted.
- Shulchan Aruch text includes the commentary reference letters, e.g. `(א') א (א)`. These are not part of the text.
- Text markers:
  - `=...=` explains an abbreviation.
  - `/.../` is a note from the Responsa Project.
  - `%...%` stands for the original's square brackets.

## When something fails

- **"Responsa was not found":** the user must have Responsa installed. Setting `RESPONSA_MCP_EXE` to the full path of `RESPONSA.exe` overrides the search for it.
- **A start-up message about the disk:** the Responsa Disk-on-Key must be connected.
- **"unknown search_id":** only the last 3 searches are kept, and none survive a restart. Search again.
- `close_windows` cleans up the private instance if things look stuck.
- **Automatic restart.** If an operation gets stuck (a search that does not finish within `timeout_sec`, a window that does not open), or Responsa shows an internal error or an unknown window, the tool restarts its private instance at once. The error message then ends with "The private Responsa instance was restarted; try again."
  - Earlier `search_id`s are gone after a restart: search again.
  - Give slow searches a larger `timeout_sec` rather than retrying with a small one.
