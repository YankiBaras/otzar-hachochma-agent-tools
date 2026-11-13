// IT-780: the MCP tool definitions (names, descriptions, input schemas). IT-789: moved out of Program.cs so the
// offline tests can check the schema; adds `books` to search.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ResponsaMcp
{
    internal static class ToolDefs
    {
        static Dictionary<string, object> Obj(Dictionary<string, object> props, params string[] required)
        {
            var d = new Dictionary<string, object> { { "type", "object" }, { "properties", props }, { "additionalProperties", false } };
            if (required.Length > 0) d["required"] = new List<object>(required);
            return d;
        }
        static Dictionary<string, object> Str(int maxLen, string desc) { return new Dictionary<string, object> { { "type", "string" }, { "maxLength", maxLen }, { "description", desc } }; }
        static Dictionary<string, object> Int(int min, int max, string desc) { return new Dictionary<string, object> { { "type", "integer" }, { "minimum", min }, { "maximum", max }, { "description", desc } }; }
        static Dictionary<string, object> StrList(int maxItems, int maxLen, string desc)
        {
            return new Dictionary<string, object> { { "type", "array" }, { "minItems", 1 }, { "maxItems", maxItems },
                { "items", new Dictionary<string, object> { { "type", "string" }, { "maxLength", maxLen } } }, { "description", desc } };
        }
        static Dictionary<string, object> Bool(string desc) { return new Dictionary<string, object> { { "type", "boolean" }, { "description", desc } }; }
        static int I(Dictionary<string, object> a, string k, int def) { return a.ContainsKey(k) ? (int)a[k] : def; }

        public static List<ToolDef> Build(Tools t)
        {
            return new List<ToolDef> {
                new ToolDef {
                    Name = "status",
                    Description = "Start (if needed) the tool's private Responsa instance and report the installation, version, instance and open searches. The instance runs on a hidden desktop; the user's own Responsa is never touched.",
                    Schema = Obj(new Dictionary<string, object>()),
                    Run = a => t.Status() },
                new ToolDef {
                    Name = "search",
                    Description = "Full-text search in Responsa with its ADVANCED query syntax (see the responsa skill), e.g. '#נר [1:3] #שבת', '5: %קדש #שם', 'משפט: $מנהג $שבת'. Returns the total, the number of results per database, and the first results (source line + snippet). Responsa refuses searches with more than 32,000 results: narrow the query. To search only in some books, pass `books` (names from Responsa's database tree, e.g. ['דניאל'] or ['רש\"י ומפרשיו / רש\"י / דניאל']); the reply's `scope` lists what was searched.",
                    Schema = Obj(new Dictionary<string, object> {
                        { "query", Str(300, "Query in Responsa advanced syntax, typed in logical order.") },
                        { "all_databases", Bool("true = search all databases (recommended); false = only the databases selected in Responsa; omitted = leave Responsa's setting. Not with books.") },
                        { "books", StrList(BookMatch.MaxBooks, 120, "Search only these books (IT-789). Each item is a name from Responsa's database tree (a category, a book, a commentator), or a path of names separated by '/' when a name is in several places, e.g. 'תנ\"ך / דניאל', 'רש\"י ומפרשיו / רש\"י / דניאל'. A name that matches nothing or several places is an error that lists candidates; nothing is searched then.") },
                        { "limit", Int(0, Tools.MaxResultsPerCall, "How many results to return now (default 20).") },
                        { "timeout_sec", Int(10, 600, "Default 180.") } }, "query"),
                    Run = a => t.Search((string)a["query"], a.ContainsKey("all_databases") ? ((bool)a["all_databases"] ? 1 : 0) : -1, a.ContainsKey("books") ? (List<string>)a["books"] : null, I(a, "limit", 20), I(a, "timeout_sec", 180)) },
                new ToolDef {
                    Name = "get_results",
                    Description = "More results of an earlier search (source line + snippet), by position. Only the last 3 searches are kept.",
                    Schema = Obj(new Dictionary<string, object> {
                        { "search_id", Int(1, 1000000, "search_id returned by search.") },
                        { "from", Int(1, 32000, "First result number (1-based).") },
                        { "count", Int(1, Tools.MaxResultsPerCall, "How many (default 20).") } }, "search_id", "from"),
                    Run = a => t.GetResults((int)a["search_id"], (int)a["from"], I(a, "count", 20)) },
                new ToolDef {
                    Name = "open_text",
                    Description = "Text of one unit (amud, chapter, siman, teshuva...) by reference, as typed in Responsa's 'כתיבת מקורות', e.g. 'בבלי ברכות כז ב', 'רמבם תפילה ד א', 'שוע אוח רסג', 'משנה ברורה רסג'. Use a source line from search results to read a result in full. Returns Responsa's source title and the text.",
                    Schema = Obj(new Dictionary<string, object> {
                        { "reference", Str(120, "Reference in Hebrew, no commas; quotation marks optional (רמבם, שוע).") },
                        { "max_chars", Int(500, Tools.MaxTextChars, "Maximum characters of text to return (default 20000).") },
                        { "timeout_sec", Int(5, 120, "Default 30.") } }, "reference"),
                    Run = a => t.OpenText((string)a["reference"], I(a, "max_chars", 20000), I(a, "timeout_sec", 30)) },
                new ToolDef {
                    Name = "close_windows",
                    Description = "Close all result and text windows in the tool's private Responsa instance (housekeeping; search_ids become invalid).",
                    Schema = Obj(new Dictionary<string, object>()),
                    Run = a => t.CloseWindows() },
            };
        }
    }
}
