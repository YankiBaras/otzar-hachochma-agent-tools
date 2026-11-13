// IT-780: parsing of the files Responsa writes with File → Save (Unicode text).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ResponsaMcp
{
    internal sealed class ResultItem
    {
        public int Number;
        public string Source;
        public string Snippet;
    }

    internal static class SaveParse
    {
        /// <summary>Reads a saved file: UTF-16 with BOM as Responsa writes it; falls back to UTF-8 / cp1255.</summary>
        public static string ReadSaved(string path) { return ReadSaved(path, 10000); }

        /// <summary>
        /// IT-789 (found by the live test): Ui.SaveVia can return while Responsa still has the file open for writing
        /// ("being used by another process"). The file is read only once it can be opened with no sharing, i.e. once
        /// Responsa has closed it, so a half-written file is never parsed. Waits up to waitMs, then fails as before.
        /// </summary>
        public static string ReadSaved(string path, int waitMs)
        {
            byte[] b = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (b == null)
            {
                try
                {
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        b = new byte[f.Length];
                        int n = 0;
                        while (n < b.Length) { int r = f.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                    }
                }
                catch (IOException)
                {
                    if (!File.Exists(path) || sw.ElapsedMilliseconds >= waitMs) throw;
                    System.Threading.Thread.Sleep(100);
                }
            }
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            return Encoding.GetEncoding(1255).GetString(b);
        }

        static readonly Regex ItemLine = new Regex(@"^\s?(\d+)\.\s{2}(\S.*?)\s*$");

        /// <summary>
        /// Results file:  "החיפוש : &lt;query&gt;", a line of '=', then for each result
        /// " N.  &lt;source line&gt;" followed by snippet lines.
        /// </summary>
        public static List<ResultItem> ParseResults(string text, out string query)
        {
            query = null;
            var items = new List<ResultItem>();
            ResultItem cur = null; var snippet = new StringBuilder();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (query == null && line.StartsWith("החיפוש :")) { query = line.Substring("החיפוש :".Length).Trim(); continue; }
                if (cur == null && (line.Trim().Length == 0 || Regex.IsMatch(line.Trim(), "^=+$"))) continue;
                var m = ItemLine.Match(line);
                if (m.Success && (cur == null || int.Parse(m.Groups[1].Value) > cur.Number))
                {
                    if (cur != null) { cur.Snippet = snippet.ToString().Trim(); items.Add(cur); }
                    cur = new ResultItem { Number = int.Parse(m.Groups[1].Value), Source = m.Groups[2].Value.Trim() };
                    snippet.Length = 0;
                    continue;
                }
                if (cur != null && line.Trim().Length > 0) { if (snippet.Length > 0) snippet.Append(' '); snippet.Append(line.Trim()); }
            }
            if (cur != null) { cur.Snippet = snippet.ToString().Trim(); items.Add(cur); }
            return items;
        }

        /// <summary>Text file: source title, a line of '=', the text, and a closing line of '*'.</summary>
        public static string ParseText(string text, out string title)
        {
            title = null;
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            int start = 0;
            if (lines.Count > 1 && Regex.IsMatch(lines[1].Trim(), "^=+$")) { title = lines[0].Trim(); start = 2; }
            int end = lines.Count;
            while (end > start && (lines[end - 1].Trim().Length == 0 || Regex.IsMatch(lines[end - 1].Trim(), @"^[\*\s]+$"))) end--;
            var sb = new StringBuilder();
            for (int i = start; i < end; i++) sb.Append(lines[i].TrimEnd()).Append('\n');
            return sb.ToString().Trim();
        }
    }
}
