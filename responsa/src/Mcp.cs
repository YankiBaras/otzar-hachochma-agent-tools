// IT-780: MCP over stdio (JSON-RPC 2.0, one message per line). No network listener.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ResponsaMcp
{
    internal sealed class ToolDef
    {
        public string Name, Description;
        public Dictionary<string, object> Schema;
        public Func<Dictionary<string, object>, object> Run;
    }

    internal sealed class McpServer
    {
        public const string Name = "responsa";
        public const string Version = "1.0.0";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        readonly List<ToolDef> tools;
        readonly TextWriter log;

        public McpServer(List<ToolDef> tools, TextWriter log) { this.tools = tools; this.log = log; }

        /// <summary>JSON text. JavaScriptSerializer escapes ' &lt; &gt; &amp; as \u00xx; those need no escaping in JSON,
        /// and Hebrew abbreviations are full of apostrophes, so they are written plainly (fewer tokens).</summary>
        public static string Serialize(object o)
        {
            return Json.Serialize(o).Replace("\\u0027", "'").Replace("\\u003c", "<").Replace("\\u003e", ">").Replace("\\u0026", "&");
        }

        /// <summary>Handles one JSON-RPC line; returns the response line, or null for notifications.</summary>
        public string Handle(string line)
        {
            Dictionary<string, object> req;
            try { req = Json.Deserialize<Dictionary<string, object>>(line); }
            catch { return Error(null, -32700, "parse error"); }
            if (req == null) return Error(null, -32600, "invalid request");
            object id; req.TryGetValue("id", out id);
            string method = req.ContainsKey("method") ? req["method"] as string : null;
            var prms = req.ContainsKey("params") ? req["params"] as Dictionary<string, object> : null;
            if (method == null) return id == null ? null : Error(id, -32600, "invalid request");
            switch (method)
            {
                case "initialize":
                    string pv = prms != null && prms.ContainsKey("protocolVersion") ? prms["protocolVersion"] as string : null;
                    return Result(id, new Dictionary<string, object> {
                        { "protocolVersion", pv ?? "2024-11-05" },
                        { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                        { "serverInfo", new Dictionary<string, object> { { "name", Name }, { "version", Version } } },
                        { "instructions", "Bar-Ilan Responsa Project (שו\"ת בר אילן) v29 on this PC, in a private background instance. Use the 'responsa' skill for the query language and reference format. Cite the source line Responsa returns." } });
                case "ping":
                    return Result(id, new Dictionary<string, object>());
                case "tools/list":
                    return Result(id, new Dictionary<string, object> { { "tools", tools.Select(t => (object)new Dictionary<string, object> { { "name", t.Name }, { "description", t.Description }, { "inputSchema", t.Schema } }).ToList() } });
                case "tools/call":
                    return CallTool(id, prms);
                default:
                    if (method.StartsWith("notifications/")) return null;
                    return id == null ? null : Error(id, -32601, "method not found: " + method);
            }
        }

        string CallTool(object id, Dictionary<string, object> prms)
        {
            string name = prms != null && prms.ContainsKey("name") ? prms["name"] as string : null;
            var tool = tools.FirstOrDefault(t => t.Name == name);
            if (tool == null) return Error(id, -32602, "unknown tool: " + name);
            var args = prms.ContainsKey("arguments") && prms["arguments"] != null ? prms["arguments"] as Dictionary<string, object> : new Dictionary<string, object>();
            if (args == null) return ToolResult(id, "arguments must be an object", true);
            try
            {
                var clean = Validate(tool.Schema, args);
                object result = tool.Run(clean);
                return ToolResult(id, Serialize(result), false);
            }
            catch (ToolError e) { return ToolResult(id, e.Message, true); }
            catch (Exception e) { log.WriteLine("[responsa-mcp] " + e); return ToolResult(id, "internal error: " + e.Message, true); }
        }

        /// <summary>Checks args against the tool schema: object, known keys, required keys, types and ranges.</summary>
        public static Dictionary<string, object> Validate(Dictionary<string, object> schema, Dictionary<string, object> args)
        {
            var props = (Dictionary<string, object>)schema["properties"];
            var required = schema.ContainsKey("required") ? ((IEnumerable)schema["required"]).Cast<object>().Select(o => (string)o).ToList() : new List<string>();
            var clean = new Dictionary<string, object>();
            foreach (var kv in args)
            {
                if (!props.ContainsKey(kv.Key)) throw new ToolError("unknown argument: " + kv.Key);
                var p = (Dictionary<string, object>)props[kv.Key];
                string type = (string)p["type"];
                object v = kv.Value;
                if (type == "string")
                {
                    var s = v as string;
                    if (s == null) throw new ToolError(kv.Key + " must be a string");
                    s = s.Trim();
                    if (s.Length == 0) throw new ToolError(kv.Key + " must not be empty");
                    if (p.ContainsKey("maxLength") && s.Length > Convert.ToInt32(p["maxLength"])) throw new ToolError(kv.Key + " is too long (max " + p["maxLength"] + " characters)");
                    if (s.IndexOfAny(new[] { '\r', '\n', '\t', '\0' }) >= 0) throw new ToolError(kv.Key + " must be a single line");
                    clean[kv.Key] = s;
                }
                else if (type == "integer")
                {
                    if (!(v is int || v is long || (v is decimal && (decimal)v == Math.Floor((decimal)v)))) throw new ToolError(kv.Key + " must be an integer");
                    long n = Convert.ToInt64(v);
                    long min = p.ContainsKey("minimum") ? Convert.ToInt64(p["minimum"]) : long.MinValue;
                    long max = p.ContainsKey("maximum") ? Convert.ToInt64(p["maximum"]) : long.MaxValue;
                    if (n < min || n > max) throw new ToolError(kv.Key + " must be between " + min + " and " + max);
                    clean[kv.Key] = (int)n;
                }
                else if (type == "boolean")
                {
                    if (!(v is bool)) throw new ToolError(kv.Key + " must be true or false");
                    clean[kv.Key] = v;
                }
            }
            foreach (var r in required) if (!clean.ContainsKey(r)) throw new ToolError("missing argument: " + r);
            return clean;
        }

        static string Result(object id, object result) { return Serialize(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } }); }
        static string Error(object id, int code, string message)
        {
            return Serialize(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } } });
        }
        static string ToolResult(object id, string text, bool isError)
        {
            return Result(id, new Dictionary<string, object> {
                { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                { "isError", isError } });
        }
    }
}
