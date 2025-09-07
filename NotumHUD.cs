// NotumHUD — WebUI HUD (Stable UI, pins, delta blink, overlay, theming hook, AC grid fix, bold themes)
// - Web UI:   http://127.0.0.1:8777/
// - Overlay:  http://127.0.0.1:8777/overlay
//
// Features
// • Clean dashboard (AAO, AAD, Crit+, XP%, HP, Nano, +Damage, Armor Classes)
// • Pins with delta blink, persistence, and 0/12345678 filtering
// • Grouped stats browser with search (auto-opens matching groups), hide-per-item (persisted)
// • Rate cap knob (250/500/1000) + numeric interval (persisted)
// • Mini overlay window, draggable, theme-aware
// • Theming hook via NotumHUD.themes.json + several daring built-in themes
// • AC grid auto-expands downward; numeric pills never overflow
//
// Persistence file: NotumHUD.config.json (next to DLL)
//
// Refs: AOSharp.Core.dll, AOSharp.Common.dll | TargetFramework: .NET 4.7.2 (C# 7.3)

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AOSharp.Core;
using AOSharp.Core.UI;
using AOSharp.Common.GameData;

public class Main : AOPluginEntry
{
    private static bool _enabled = true;
    private static float _accum;
    private static float _intervalSec = 0.5f; // persisted
    private static double _timeSeconds = 0.0;
    private static double _lastBroadcastAt = 0.0;
    private static double _broadcastEvery = 0.5;
    private static readonly int _port = 8777;

    // persistence
    private static string _pluginDir = "";
    private static string _configPath = "";
    private static string _themesPath = "";
    private static readonly HashSet<string> _pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _hiddenMisc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string _theme = "theme-neon"; // default
    private static bool _compact = false;

    // Core display names
    private static readonly string[] CoreStatNames = { "AddAllOff", "AddAllDef", "CriticalIncrease", "XPModifier" };
    private static readonly string[] DmgStatNames = {
        "ProjectileDamageModifier","MeleeDamageModifier","EnergyDamageModifier","ChemicalDamageModifier",
        "RadiationDamageModifier","ColdDamageModifier","FireDamageModifier","PoisonDamageModifier"
    };
    private static readonly Tuple<string, string>[] AcStats = {
        Tuple.Create("ProjectileAC","Proj"), Tuple.Create("MeleeAC","Melee"),
        Tuple.Create("EnergyAC","Energy"),   Tuple.Create("ChemicalAC","Chem"),
        Tuple.Create("RadiationAC","Rad"),   Tuple.Create("ColdAC","Cold"),
        Tuple.Create("FireAC","Fire"),       Tuple.Create("PoisonAC","Poison")
    };
    private static readonly string[] HpNowNames = { "Life", "Health", "CurrentHealth" };
    private static readonly string[] HpMaxNames = { "MaxHealth", "LifeMax", "MaxLife" };
    private static readonly string[] NanoNowNames = { "NanoEnergy", "Nano", "CurrentNano" };
    private static readonly string[] NanoMaxNames = { "MaxNanoEnergy", "MaxNano", "NanoMax" };

    // Web server/SSE
    private static HttpListener _http;
    private static Thread _httpThread;
    private class SseClient { public HttpListenerResponse Resp; public StreamWriter Writer; }
    private static readonly List<SseClient> _sseClients = new List<SseClient>();
    private static readonly object _sseLock = new object();
    private static string _lastSnapshotJson = "{}";

    [Obsolete("AOSharp requires overriding an obsolete Run signature.")]
    public override void Run(string pluginDir)
    {
        _pluginDir = pluginDir ?? AppDomain.CurrentDomain.BaseDirectory;
        _configPath = Path.Combine(_pluginDir, "NotumHUD.config.json");
        _themesPath = Path.Combine(_pluginDir, "NotumHUD.themes.json");
        LoadConfig();

        Chat.RegisterCommand("stat", delegate (string cmd, string[] a, ChatWindow cw)
        {
            cw.WriteLine("NotumHUD: http://127.0.0.1:" + _port + "  |  Overlay: /overlay");
        });

        StartWebServer();
        Chat.WriteLine("[NotumHUD] WebUI ready at http://127.0.0.1:" + _port);
        Game.OnUpdate += OnUpdate;
    }

    public override void Teardown()
    {
        Game.OnUpdate -= OnUpdate;
        StopWebServer();
        Chat.WriteLine("[NotumHUD] stopped.");
    }

    private static void OnUpdate(object s, float dt)
    {
        _timeSeconds += dt;
        if (!_enabled) return;

        _accum += dt;
        if (_accum < _intervalSec) return;
        _accum = 0f;

        try
        {
            if (DynelManager.LocalPlayer == null) return;
            var all = ReadAllStats();
            _lastSnapshotJson = BuildSnapshotJson(all);
            if (_timeSeconds - _lastBroadcastAt >= _broadcastEvery)
            {
                _lastBroadcastAt = _timeSeconds;
                BroadcastSse(_lastSnapshotJson);
            }
        }
        catch (Exception ex) { Chat.WriteLine("[NotumHUD] Update error: " + ex.Message); }
    }

    private static Dictionary<string, int> ReadAllStats()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Array vals = Enum.GetValues(typeof(Stat));
        for (int i = 0; i < vals.Length; i++)
        {
            var st = (Stat)vals.GetValue(i);
            try { d[st.ToString()] = DynelManager.LocalPlayer.GetStat(st); } catch { }
        }
        int hpNow, hpMax, npNow, npMax;
        if (TryReadAny(HpNowNames, out hpNow)) d["_HP_NOW"] = hpNow;
        if (TryReadAny(HpMaxNames, out hpMax)) d["_HP_MAX"] = hpMax;
        if (TryReadAny(NanoNowNames, out npNow)) d["_NP_NOW"] = npNow;
        if (TryReadAny(NanoMaxNames, out npMax)) d["_NP_MAX"] = npMax;
        return d;
    }

    private static bool TryReadStat(string name, out int value)
    {
        value = 0;
        try
        {
            Stat id; if (!Enum.TryParse(name, out id)) return false;
            if (DynelManager.LocalPlayer == null) return false;
            value = DynelManager.LocalPlayer.GetStat(id);
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadAny(string[] names, out int value)
    {
        for (int i = 0; i < names.Length; i++) if (TryReadStat(names[i], out value)) return true;
        value = 0; return false;
    }

    private static bool Meaningful(int v) { return v != 0 && v != 12345678; }
    private static int Get(Dictionary<string, int> d, string k) { int v; return d.TryGetValue(k, out v) ? v : 0; }
    private static int SafePct(int a, int b) { if (b <= 0) return 0; int p = (int)Math.Round((a * 100.0) / b); if (p < 0) p = 0; if (p > 100) p = 100; return p; }
    private static string EscapeJson(string s) { if (string.IsNullOrEmpty(s)) return ""; return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }
    private static string Format1(double v) { return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'); }

    private static readonly Dictionary<string, string> _labelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        {"TwoHandedEdged","2HE"},{"OneHandedEdged","1HE"},{"TwoHandedBlunt","2HB"},{"OneHandedBlunt","1HB"},
        {"MeleeEnergy","ME"},{"RangedEnergy","RE"},{"FullAuto","Full Auto"},{"FlingShot","Fling"},
        {"AimedShot","Aimed Shot"},{"ComputerLiteracy","Comp Lit"},{"NanoCInit","Nano Init"},
        {"NanoProg","Nano Programming"},{"BodyDev","Body Dev"},{"DuckExp","Duck-Exp"},{"DodgeRanged","Dodge Ranged"},
        {"EvadeClsC","Evade Close"},{"AddAllOff","AAO"},{"AddAllDef","AAD"},{"CriticalIncrease","Crit+"},{"XPModifier","XP %"},
        {"MatterCreation","MC"},{"MatterMetamorphosis","MM"},{"BiologicalMetamorphosis","BM"},{"TimeAndSpace","TS"},
        {"PsychologicalModification","PM"},{"SensoryImprovement","SI"},{"SubMachineGun","SMG"},
        {"NanoResist","Nano Resist"},{"FirstAid","First Aid"},{"HealDelta","Heal Delta"},{"NanoDelta","Nano Delta"},{"Treatment","Treatment"}
    };
    private static string LabelFor(string statName)
    {
        string pretty; if (_labelMap.TryGetValue(statName, out pretty)) return pretty;
        var sb = new StringBuilder(statName.Length + 8);
        for (int i = 0; i < statName.Length; i++) { char c = statName[i]; if (i > 0 && char.IsUpper(c) && char.IsLower(statName[i - 1])) sb.Append(' '); sb.Append(c); }
        return sb.ToString();
    }

    private static string BuildSnapshotJson(Dictionary<string, int> s)
    {
        var sb = new StringBuilder(65536);
        sb.Append('{');

        // core cards
        sb.Append("\"core\":{"); bool firstCore = true;
        for (int i = 0; i < CoreStatNames.Length; i++)
        {
            int v; if (s.TryGetValue(CoreStatNames[i], out v))
            { if (!firstCore) sb.Append(','); firstCore = false; sb.Append('\"').Append(CoreStatNames[i]).Append("\":").Append(v); }
        }
        sb.Append("},");

        // hp / nano
        int hpNow = Get(s, "_HP_NOW"), hpMax = Get(s, "_HP_MAX");
        int npNow = Get(s, "_NP_NOW"), npMax = Get(s, "_NP_MAX");
        sb.Append("\"hp\":{");
        sb.Append("\"now\":").Append(hpNow).Append(",\"max\":").Append(hpMax).Append(",\"pct\":").Append(SafePct(hpNow, hpMax)).Append("},");
        sb.Append("\"nano\":{");
        sb.Append("\"now\":").Append(npNow).Append(",\"max\":").Append(npMax).Append(",\"pct\":").Append(SafePct(npNow, npMax)).Append("},");

        // +Damage (filtered)
        sb.Append("\"dmg\":{"); bool firstDmg = true;
        for (int i = 0; i < DmgStatNames.Length; i++)
        {
            int v; if (s.TryGetValue(DmgStatNames[i], out v) && Meaningful(v))
            { if (!firstDmg) sb.Append(','); firstDmg = false; sb.Append('\"').Append(DmgStatNames[i]).Append("\":").Append(v); }
        }
        sb.Append("},");

        // ACs (filtered)
        sb.Append("\"ac\":{"); bool firstAc = true;
        for (int i = 0; i < AcStats.Length; i++)
        {
            int v; if (s.TryGetValue(AcStats[i].Item1, out v) && Meaningful(v))
            { if (!firstAc) sb.Append(','); firstAc = false; sb.Append('\"').Append(AcStats[i].Item1).Append("\":").Append(v); }
        }
        sb.Append("},");

        // Pins (filtered, persisted)
        sb.Append("\"pins\":["); bool firstPin = true;
        foreach (var name in _pins)
        {
            int val; s.TryGetValue(name, out val);
            if (!Meaningful(val)) continue;
            if (!firstPin) sb.Append(',');
            firstPin = false;
            sb.Append("{\"name\":\"").Append(EscapeJson(name)).Append("\",\"v\":").Append(val).Append(",\"label\":\"").Append(LabelFor(name)).Append("\"}");
        }
        sb.Append("],");

        // ALL stats (filtered map + names)
        sb.Append("\"all\":{"); bool firstAll = true;
        var names = new List<string>(s.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var allNames = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            string k = names[i];
            if (k.StartsWith("_")) continue;
            int v = s[k];
            if (!Meaningful(v)) continue;
            if (!firstAll) sb.Append(',');
            firstAll = false;
            sb.Append('\"').Append(EscapeJson(k)).Append("\":").Append(v);
            allNames.Add(k);
        }
        sb.Append("},");

        sb.Append("\"all_names\":["); for (int i = 0; i < allNames.Count; i++) { if (i > 0) sb.Append(','); sb.Append('\"').Append(EscapeJson(allNames[i])).Append('\"'); }
        sb.Append("],");

        // hidden misc (persisted)
        sb.Append("\"hiddenMisc\":["); bool fh = true; foreach (var h in _hiddenMisc) { if (!fh) sb.Append(','); fh = false; sb.Append('\"').Append(EscapeJson(h)).Append('\"'); }
        sb.Append("],");

        // settings + time
        sb.Append("\"settings\":{");
        sb.Append("\"enabled\":").Append(_enabled ? "true" : "false").Append(',');
        sb.Append("\"interval_ms\":").Append((int)(_intervalSec * 1000.0f)).Append(',');
        sb.Append("\"theme\":\"").Append(_theme).Append("\",");
        sb.Append("\"compact\":").Append(_compact ? "true" : "false");
        sb.Append("},");
        sb.Append("\"t\":").Append(Format1(_timeSeconds));
        sb.Append('}');
        return sb.ToString();
    }

    // ===================== Web server =====================
    private static void StartWebServer()
    {
        try
        {
            _http = new HttpListener();
            _http.Prefixes.Add("http://127.0.0.1:" + _port + "/");
            _http.Start();
            _httpThread = new Thread(HttpLoop) { IsBackground = true };
            _httpThread.Start();
        }
        catch (Exception ex) { Chat.WriteLine("[NotumHUD] HTTP start failed: " + ex.Message); }
    }
    private static void StopWebServer()
    {
        try { if (_http != null) { _http.Stop(); _http.Close(); _http = null; } } catch { }
        lock (_sseLock)
        {
            for (int i = 0; i < _sseClients.Count; i++)
            {
                try { _sseClients[i].Writer.Flush(); _sseClients[i].Resp.OutputStream.Close(); _sseClients[i].Resp.Close(); } catch { }
            }
            _sseClients.Clear();
        }
    }
    private static void HttpLoop()
    {
        while (_http != null && _http.IsListening)
        {
            HttpListenerContext ctx = null;
            try { ctx = _http.GetContext(); } catch { break; }
            if (ctx == null) break;
            ThreadPool.QueueUserWorkItem(HandleRequest, ctx);
        }
    }
    private static void HandleRequest(object state)
    {
        var ctx = (HttpListenerContext)state;
        string path = ctx.Request.Url.AbsolutePath;
        try
        {
            if (path == "/" || path == "/index.html") { WriteBytes(ctx, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(_indexHtml)); return; }
            if (path == "/app.css") { WriteBytes(ctx, "text/css; charset=utf-8", Encoding.UTF8.GetBytes(_appCss)); return; }
            if (path == "/app.js") { WriteBytes(ctx, "application/javascript; charset=utf-8", Encoding.UTF8.GetBytes(_appJs)); return; }
            if (path == "/overlay") { WriteBytes(ctx, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(_overlayHtml)); return; }
            if (path == "/overlay.js") { WriteBytes(ctx, "application/javascript; charset=utf-8", Encoding.UTF8.GetBytes(_overlayJs)); return; }
            if (path == "/events")
            {
                ctx.Response.StatusCode = 200; ctx.Response.KeepAlive = true; ctx.Response.SendChunked = true;
                ctx.Response.AddHeader("Cache-Control", "no-cache"); ctx.Response.AddHeader("Connection", "keep-alive");
                ctx.Response.ContentType = "text/event-stream";
                var sw = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };
                lock (_sseLock) { _sseClients.Add(new SseClient { Resp = ctx.Response, Writer = sw }); }
                try { sw.WriteLine("retry: 1000"); sw.WriteLine("data: " + _lastSnapshotJson); sw.WriteLine(); sw.Flush(); } catch { }
                try { while (_http != null && _http.IsListening) { Thread.Sleep(15000); sw.WriteLine(": ping"); sw.WriteLine(); sw.Flush(); } }
                catch { }
                finally { RemoveClient(ctx); }
                return;
            }
            if (path == "/api/state") { WriteBytes(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_lastSnapshotJson)); return; }

            // Settings/commands
            if (path == "/api/cmd")
            {
                NameValueCollection qs = ctx.Request.QueryString;
                ApplyCommand(qs["action"], qs["value"]);
                WriteBytes(ctx, "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}")); return;
            }

            // Theming hook: serve raw JSON array for client to parse
            if (path == "/api/themes")
            {
                try
                {
                    if (File.Exists(_themesPath))
                    {
                        var txt = File.ReadAllText(_themesPath, Encoding.UTF8);
                        WriteBytes(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(txt));
                    }
                    else
                    {
                        WriteBytes(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("[]"));
                    }
                }
                catch
                {
                    WriteBytes(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("[]"));
                }
                return;
            }

            ctx.Response.StatusCode = 404; ctx.Response.Close();
        }
        catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
    }
    private static void RemoveClient(HttpListenerContext ctx)
    {
        lock (_sseLock)
        {
            for (int i = 0; i < _sseClients.Count; i++)
            {
                if (_sseClients[i].Resp == ctx.Response) { _sseClients.RemoveAt(i); break; }
            }
        }
        try { ctx.Response.OutputStream.Close(); ctx.Response.Close(); } catch { }
    }
    private static void WriteBytes(HttpListenerContext ctx, string type, byte[] data)
    {
        ctx.Response.ContentType = type; ctx.Response.ContentLength64 = data.LongLength;
        ctx.Response.OutputStream.Write(data, 0, data.Length);
        ctx.Response.OutputStream.Flush(); ctx.Response.Close();
    }

    private static void ApplyCommand(string action, string value)
    {
        if (string.IsNullOrEmpty(action)) return;

        if (action == "enable") { _enabled = Truthy(value); return; }
        if (action == "interval_ms")
        {
            int ms = ToInt(value, (int)(_intervalSec * 1000.0f)); if (ms < 100) ms = 100; if (ms > 5000) ms = 5000;
            _intervalSec = ms / 1000f; SaveConfig(); return;
        }
        if (action == "theme") { string v = (value ?? "").Trim(); if (!string.IsNullOrEmpty(v)) { _theme = v; SaveConfig(); } return; }
        if (action == "compact") { _compact = Truthy(value); SaveConfig(); return; }

        if (action == "pin_add") { if (!string.IsNullOrEmpty(value)) { _pins.Add(value); SaveConfig(); } return; }
        if (action == "pin_remove") { if (!string.IsNullOrEmpty(value)) { _pins.Remove(value); SaveConfig(); } return; }

        if (action == "misc_hide_add") { if (!string.IsNullOrEmpty(value)) { _hiddenMisc.Add(value); SaveConfig(); } return; }
        if (action == "misc_hide_remove") { if (!string.IsNullOrEmpty(value)) { _hiddenMisc.Remove(value); SaveConfig(); } return; }
        if (action == "misc_hide_clear") { _hiddenMisc.Clear(); SaveConfig(); return; }
    }

    private static bool Truthy(string v) { if (string.IsNullOrEmpty(v)) return false; int i; if (int.TryParse(v, out i)) return i != 0; string s = v.ToLowerInvariant(); return s == "true" || s == "on" || s == "1" || s == "yes"; }
    private static int ToInt(string v, int def) { if (string.IsNullOrEmpty(v)) return def; int i; return int.TryParse(v, out i) ? i : def; }
    private static void BroadcastSse(string json)
    {
        lock (_sseLock)
        {
            for (int i = _sseClients.Count - 1; i >= 0; i--)
            {
                try { _sseClients[i].Writer.WriteLine("data: " + json); _sseClients[i].Writer.WriteLine(); _sseClients[i].Writer.Flush(); }
                catch { try { _sseClients[i].Resp.OutputStream.Close(); _sseClients[i].Resp.Close(); } catch { } _sseClients.RemoveAt(i); }
            }
        }
    }

    // =============== Config I/O (tiny manual JSON) ===============
    private static void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            string txt = File.ReadAllText(_configPath, Encoding.UTF8);
            ExtractList(txt, "pins", _pins);
            ExtractList(txt, "hiddenMisc", _hiddenMisc);
            string theme = ExtractString(txt, "theme"); if (!string.IsNullOrEmpty(theme)) _theme = theme;
            bool? comp = ExtractBool(txt, "compact"); if (comp.HasValue) _compact = comp.Value;
            int? iv = ExtractInt(txt, "interval_ms"); if (iv.HasValue) { int ms = Math.Min(5000, Math.Max(100, iv.Value)); _intervalSec = ms / 1000f; }
        }
        catch (Exception ex) { Chat.WriteLine("[NotumHUD] Config load failed: " + ex.Message); }
    }

    private static void SaveConfig()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\n");

            // pins
            sb.Append("  \"pins\":[");
            bool first = true;
            foreach (var p in _pins) { if (!first) sb.Append(','); first = false; sb.Append('\"').Append(EscapeJson(p)).Append('\"'); }
            sb.Append("],\n");

            // hidden misc
            sb.Append("  \"hiddenMisc\":[");
            first = true;
            foreach (var h in _hiddenMisc) { if (!first) sb.Append(','); first = false; sb.Append('\"').Append(EcapeJson(h)).Append('\"'); }
            sb.Append("],\n");

            // ui prefs
            sb.Append("  \"theme\":\"").Append(_theme).Append("\",\n");
            sb.Append("  \"compact\":").Append(_compact ? "true" : "false").Append(",\n");
            sb.Append("  \"interval_ms\":").Append((int)(_intervalSec * 1000.0f)).Append('\n');

            sb.Append("}\n");
            File.WriteAllText(_configPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Chat.WriteLine("[NotumHUD] Config save failed: " + ex.Message); }
    }

    // Typo-safe helper
    private static string EcapeJson(string s) { return EscapeJson(s); }

    private static void ExtractList(string json, string key, HashSet<string> target)
    {
        try
        {
            var rx = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            var m = rx.Match(json);
            if (!m.Success) return;
            string inner = m.Groups[1].Value;
            var rxItem = new Regex("\"((?:\\\\\"|[^\"])*)\"");
            var mc = rxItem.Matches(inner);
            target.Clear();
            for (int i = 0; i < mc.Count; i++)
            {
                string val = mc[i].Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                if (!string.IsNullOrEmpty(val)) target.Add(val);
            }
        }
        catch { }
    }
    private static string ExtractString(string json, string key)
    {
        try
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\\"|[^\"])*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        catch { return null; }
    }
    private static bool? ExtractBool(string json, string key)
    {
        try
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            if (!m.Success) return null;
            return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
    }
    private static int? ExtractInt(string json, string key)
    {
        try
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            if (!m.Success) return null;
            int v; return int.TryParse(m.Groups[1].Value, out v) ? (int?)v : null;
        }
        catch { return null; }
    }

    // ================== Embedded UI ==================
    private static readonly string _indexHtml =
@"<!doctype html><html lang=""en""><head>
<meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>NotumHUD</title><link rel=""stylesheet"" href=""/app.css""></head>
<body>
<header class=""topbar"">
  <div class=""brand"">NotumHUD</div>
  <div class=""right"">
    <select id=""theme"">
      <option value=""theme-neon"">Neon</option>
      <option value=""theme-dark"">Terminal</option>
      <option value=""theme-light"">Paper</option>
      <option value=""theme-synth"">Synthwave</option>
      <option value=""theme-inferno"">Inferno</option>
      <option value=""theme-emerald"">Emerald</option>
      <option value=""theme-aurora"">Aurora</option>
      <option value=""theme-monokai"">Monokai</option>
    </select>
    <label class=""compact-toggle""><input type=""checkbox"" id=""compact""> Compact</label>
    <div class=""rate"">
      <span class=""muted"">Rate</span>
      <div class=""seg"">
        <button class=""segbtn"" data-rate=""250"">250</button>
        <button class=""segbtn"" data-rate=""500"">500</button>
        <button class=""segbtn"" data-rate=""1000"">1000</button>
      </div>
    </div>
    <label>Interval <input id=""interval"" type=""number"" min=""100"" max=""5000"" step=""50"" value=""500""> ms</label>
    <label><input type=""checkbox"" id=""toggle"" checked> Enabled</label>
    <div id=""status"" class=""status"">connecting…</div>
  </div>
</header>

<main class=""container"">
  <section id=""hud"" class=""grid"">
    <div class=""card glow""><div class=""title"">AAO</div><div id=""aao"" class=""big numeric"">–</div></div>
    <div class=""card glow""><div class=""title"">AAD</div><div id=""aad"" class=""big numeric"">–</div></div>
    <div class=""card glow""><div class=""title"">Crit+</div><div id=""crit"" class=""big numeric"">–</div></div>
    <div class=""card glow""><div class=""title"">XP% boost</div><div id=""xpmod"" class=""big numeric"">–</div></div>

    <div class=""card glow"">
      <div class=""title"">HP</div>
      <div class=""bar""><div id=""hpbar"" class=""barfill""></div></div>
      <div id=""hptext"" class=""muted numeric"">–</div>
    </div>
    <div class=""card glow"">
      <div class=""title"">Nano</div>
      <div class=""bar""><div id=""nanobar"" class=""barfill""></div></div>
      <div id=""nanotext"" class=""muted numeric"">–</div>
    </div>

    <div class=""card glow""><div class=""title"">+Damage</div><div id=""dmgchips"" class=""chips""></div></div>
    <div class=""card glow""><div class=""title"">Armor Classes</div><div id=""acs"" class=""acgrid""></div></div>

    <div class=""wide2 card glow"">
      <div class=""title"">Pinned Stats</div>
      <div id=""pins"" class=""pins"">None pinned yet. Use the groups to pin.</div>
    </div>
  </section>

  <section class=""panel"">
    <h2>Stats by Group</h2>
    <div class=""controls"">
      <input id=""filter"" placeholder=""Search (e.g. 2HE, Comp Lit, Nano Resist, Treatment)"" />
      <span class=""muted"">Click to pin/unpin. Empty (0/12345678) hidden automatically.</span>
    </div>

    <details id=""g-abilities""><summary>Base Abilities</summary><div class=""kvlist"" id=""list-abilities""></div></details>
    <details id=""g-melee""><summary>Melee & Specials</summary><div class=""kvlist"" id=""list-melee""></div></details>
    <details id=""g-ranged""><summary>Ranged & Specials</summary><div class=""kvlist"" id=""list-ranged""></div></details>
    <details id=""g-nano""><summary>Nano</summary><div class=""kvlist"" id=""list-nano""></div></details>
    <details id=""g-speeddef""><summary>Speed & Defense</summary><div class=""kvlist"" id=""list-speeddef""></div></details>
    <details id=""g-trades""><summary>Tradeskills</summary><div class=""kvlist"" id=""list-trades""></div></details>
    <details id=""g-spynav""><summary>Spying & Navigation</summary><div class=""kvlist"" id=""list-spynav""></div></details>

    <details id=""g-misc"">
      <summary>Misc <span class=""muted"">(individually closable)</span></summary>
      <div class=""controls"">
        <label><input id=""showHidden"" type=""checkbox""> Show hidden</label>
        <button id=""btnResetHidden"">Reset hidden</button>
      </div>
      <div class=""kvlist"" id=""list-misc""></div>
    </details>
  </section>
</main>

<script src=""/app.js""></script></body></html>";

    private static readonly string _appCss =
@":root{--font: 'Inter', 'SF Pro Text', 'Segoe UI', Roboto, Helvetica, Arial, system-ui, sans-serif}

/* Neon — deep black, cyan/purple glow */
body.theme-neon{--bg:#05060a;--fg:#e9f5ff;--muted:#9bc1ff;--card:#0a0f1c;--card-br:#263657;--bar1:#c084fc;--bar2:#22d3ee;--chip:#0f1426;--chip-br:#345;--glow:#2bd9ff;--accent:#7cf}
/* Terminal — charcoal w/ phosphor green */
body.theme-dark{--bg:#0b0d0c;--fg:#e1ffe5;--muted:#8fd8a0;--card:#0f1412;--card-br:#1f2a24;--bar1:#22c55e;--bar2:#16a34a;--chip:#101812;--chip-br:#1f3a29;--glow:#22ff88;--accent:#8febb0}
/* Paper — bright, warm, higher contrast */
body.theme-light{--bg:#fafbfe;--fg:#0a1220;--muted:#405066;--card:#fff;--card-br:#d9e2ef;--bar1:#fb923c;--bar2:#6366f1;--chip:#f5f7fd;--chip-br:#ccd7ea;--glow:#8793ff;--accent:#7c4bff}

/* New daring palettes */
body.theme-synth{--bg:#0a0612;--fg:#f2e9ff;--muted:#b89be6;--card:#120a1f;--card-br:#2a1b4a;--bar1:#a78bfa;--bar2:#06b6d4;--chip:#0e0920;--chip-br:#2a1b4a;--glow:#b388ff;--accent:#9d7dff}
body.theme-inferno{--bg:#080707;--fg:#ffeada;--muted:#ffbfa1;--card:#111010;--card-br:#3b2a22;--bar1:#f97316;--bar2:#dc2626;--chip:#141010;--chip-br:#3b2a22;--glow:#ff6b3d;--accent:#ff7a45}
body.theme-emerald{--bg:#06100c;--fg:#e8fff3;--muted:#9be7c1;--card:#0a1712;--card-br:#1b3327;--bar1:#10b981;--bar2:#14b8a6;--chip:#0b1511;--chip-br:#1b3327;--glow:#34f0b7;--accent:#85ffd5}
body.theme-aurora{--bg:#050815;--fg:#e9f3ff;--muted:#9cc1ff;--card:#0a1022;--card-br:#20345b;--bar1:#60a5fa;--bar2:#f472b6;--chip:#0b1226;--chip-br:#20345b;--glow:#6ea8ff;--accent:#7fb3ff}
body.theme-monokai{--bg:#0d0f10;--fg:#f8f8f2;--muted:#a6accd;--card:#131516;--card-br:#2b2f30;--bar1:#a6e22e;--bar2:#fd971f;--chip:#141618;--chip-br:#2b2f30;--glow:#ffd866;--accent:#ffeb99}

*{box-sizing:border-box}
body{margin:0;background:radial-gradient(1200px circle at 20% -10%, rgba(43,217,255,.14), transparent 60%),var(--bg);color:var(--fg);font:13.5px/1.55 var(--font)}
.topbar{display:flex;align-items:center;justify-content:space-between;padding:10px 16px;background:rgba(0,0,0,.30);border-bottom:1px solid var(--card-br);backdrop-filter:blur(8px)}
.topbar .right{display:flex;gap:12px;align-items:center}
.brand{font-weight:800;letter-spacing:.04em;text-shadow:0 0 10px var(--glow);color:var(--accent)}
.status{opacity:.85}
.rate{display:flex;align-items:center;gap:8px}
.seg{display:flex;border:1px solid var(--card-br);border-radius:10px;overflow:hidden}
.segbtn{background:var(--card);color:var(--fg);border:0;padding:4px 8px;cursor:pointer}
.segbtn + .segbtn{border-left:1px solid var(--card-br)}
.segbtn.active{background:linear-gradient(180deg, rgba(255,255,255,.06), rgba(255,255,255,.02));box-shadow:0 0 12px rgba(43,217,255,.25)}

.container{max-width:1100px;margin:22px auto;padding:0 16px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));grid-gap:14px}

.card{background:linear-gradient(180deg, rgba(255,255,255,.03), rgba(255,255,255,.01));border:1px solid var(--card-br);border-radius:14px;padding:12px;position:relative;min-height:92px}
.card.glow::after{content:'';position:absolute;inset:-1px;border-radius:14px;box-shadow:0 0 20px rgba(43,217,255,.22);pointer-events:none}
.wide2{grid-column:span 2}
.title{opacity:.9;margin-bottom:6px;font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--muted)}
.big{font-size:24px;font-weight:800;text-shadow:0 0 8px rgba(43,217,255,.25)}
.muted{opacity:.88;color:var(--muted)}

.numeric{font-variant-numeric: tabular-nums; font-feature-settings: 'tnum' 1; white-space:nowrap}

/* Bars */
.bar{height:10px;background:rgba(255,255,255,.05);border:1px solid var(--card-br);border-radius:999px;overflow:hidden}
.barfill{height:100%;background:linear-gradient(90deg,var(--bar1),var(--bar2))}

/* Chips + Pins */
.chips,.pins{display:flex;flex-wrap:wrap;gap:8px}
.chip{border:1px solid var(--chip-br);background:var(--chip);border-radius:999px;padding:4px 10px;font-size:12px;white-space:nowrap}
#pins .pin{border:1px solid var(--chip-br);background:var(--chip);border-radius:999px;padding:6px 12px;font-size:12px;display:flex;align-items:center;gap:10px;flex-wrap:nowrap;max-width:100%}
#pins .pin b{min-width:0;overflow:hidden;text-overflow:ellipsis}
#pins .pin .val{padding:4px 8px;border:1px solid var(--card-br);border-radius:999px;background:linear-gradient(180deg, rgba(255,255,255,.06), rgba(255,255,255,.02));white-space:nowrap}
#pins .pin .x{opacity:.8;cursor:pointer;flex:0 0 auto}
#pins .pin.changed{animation:pulse .45s ease-in-out}
@keyframes pulse{0%{transform:scale(1)}40%{transform:scale(1.06);box-shadow:0 0 18px rgba(43,217,255,.35)}100%{transform:scale(1)}}

/* Armor Classes — expands downward as needed */
.acgrid{
  display:grid;
  grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
  grid-auto-rows: auto;
  gap:8px;
}
.ac{
  display:flex;
  align-items:center;
  justify-content:space-between;
  gap:8px;
  border:1px solid var(--card-br);
  background:var(--card);
  border-radius:10px;
  padding:6px 8px;
  min-width:0;
}
.ac b{opacity:.95; white-space:nowrap}
.ac span{white-space:nowrap}

/* Group lists */
.kvlist{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:8px}
.kv{display:flex;align-items:center;gap:10px;border:1px solid var(--card-br);background:var(--card);border-radius:10px;padding:6px 8px;cursor:pointer;min-height:38px}
.kv:hover{box-shadow:0 0 12px rgba(43,217,255,.18)}
.kv .name{font-weight:600;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis}
.kv .val{flex:0 0 auto;text-align:right;white-space:nowrap;padding:2px 8px;border:1px solid var(--card-br);border-radius:999px;background:linear-gradient(180deg, rgba(255,255,255,.06), rgba(255,255,255,.02))}
.kv.pinned{outline:1px solid var(--glow);box-shadow:0 0 16px rgba(43,217,255,.30)}
.kv .closer{margin-left:6px;opacity:.75;border:1px solid var(--card-br);border-radius:8px;padding:2px 6px;font-size:11px;cursor:pointer;flex:0 0 auto}
.kv.hidden{opacity:.45;filter:saturate(.6)}
details>summary{cursor:pointer;user-select:none}

/* Keep group body from collapsing to 0 so groups feel stable */
.kvlist:empty { min-height: 6px; }
details[open] .kvlist { transition: opacity .12s ease; }

body.compact .panel{display:none}
body.compact .grid{grid-template-columns:repeat(auto-fit,minmax(220px,1fr))}
body.compact .card{padding:8px}
";

    private static readonly string _appJs =
@"(function(){
  var es, built=false, builtKey='';
  var els = {
    status: document.getElementById('status'),
    aao: document.getElementById('aao'), aad: document.getElementById('aad'),
    crit: document.getElementById('crit'), xpmod: document.getElementById('xpmod'),
    hpbar: document.getElementById('hpbar'), hptext: document.getElementById('hptext'),
    nanobar: document.getElementById('nanobar'), nanotext: document.getElementById('nanotext'),
    dmgchips: document.getElementById('dmgchips'), acs: document.getElementById('acs'),
    pins: document.getElementById('pins'),
    theme: document.getElementById('theme'), compact: document.getElementById('compact'),
    interval: document.getElementById('interval'), toggle: document.getElementById('toggle'),
    filter: document.getElementById('filter'),
    showHidden: document.getElementById('showHidden'), btnResetHidden: document.getElementById('btnResetHidden'),
    lists: {
      abilities: document.getElementById('list-abilities'),
      melee:     document.getElementById('list-melee'),
      ranged:    document.getElementById('list-ranged'),
      nano:      document.getElementById('list-nano'),
      speeddef:  document.getElementById('list-speeddef'),
      trades:    document.getElementById('list-trades'),
      spynav:    document.getElementById('list-spynav'),
      misc:      document.getElementById('list-misc')
    },
    groups: {
      abilities: document.getElementById('g-abilities'),
      melee:     document.getElementById('g-melee'),
      ranged:    document.getElementById('g-ranged'),
      nano:      document.getElementById('g-nano'),
      speeddef:  document.getElementById('g-speeddef'),
      trades:    document.getElementById('g-trades'),
      spynav:    document.getElementById('g-spynav'),
      misc:      document.getElementById('g-misc')
    }
  };

  // Rate buttons
  function syncRateButtons(ms){
    var btns=document.querySelectorAll('.seg [data-rate]');
    for (var i=0;i<btns.length;i++){
      var v=parseInt(btns[i].getAttribute('data-rate')||'0',10)|0;
      btns[i].classList.toggle('active', v===(ms|0));
    }
  }
  document.querySelectorAll('.seg [data-rate]').forEach(function(btn){
    btn.addEventListener('click', function(){
      var ms=parseInt(btn.getAttribute('data-rate')||'500',10)|0;
      els.interval.value=ms; syncRateButtons(ms); post('interval_ms', ms);
    });
  });

  // Server-backed settings
  function post(action, value){
    fetch('/api/cmd?action='+encodeURIComponent(action)+'&value='+encodeURIComponent(value==null?'':value), {method:'POST'});
  }
  els.interval.addEventListener('change', function(){ var v=parseInt(els.interval.value||'500',10); if (isNaN(v)) v=500; post('interval_ms', v); syncRateButtons(v); });
  els.toggle.addEventListener('change', function(){ post('enable', els.toggle.checked?1:0); });
  els.theme.addEventListener('change', function(){ document.body.classList.remove('theme-dark','theme-light','theme-neon','theme-synth','theme-inferno','theme-emerald','theme-aurora','theme-monokai'); document.body.classList.add(els.theme.value); post('theme', els.theme.value); });
  els.compact.addEventListener('change', function(){ if (els.compact.checked) document.body.classList.add('compact'); else document.body.classList.remove('compact'); post('compact', els.compact.checked?1:0); });
  els.btnResetHidden && els.btnResetHidden.addEventListener('click', function(){ post('misc_hide_clear', 1); });

  // Theming hook
  (function(){
    var dyn=document.createElement('style'); dyn.id='dyn-themes'; document.head.appendChild(dyn);
    fetch('/api/themes').then(function(r){return r.text();}).then(function(t){
      var arr=[]; try{ arr=JSON.parse(t||'[]'); }catch(e){}
      var css='';
      for (var i=0;i<arr.length;i++){
        var th=arr[i]; if (!th || !th.id || !th.vars) continue;
        var cls='body.'+th.id; css+=cls+'{';
        for (var k in th.vars){ if (Object.prototype.hasOwnProperty.call(th.vars,k)){ css+=k+':'+th.vars[k]+';'; } }
        css+='}';
        var opt=document.createElement('option'); opt.value=th.id; opt.textContent=th.label||th.id.replace(/^theme-/,''); els.theme.appendChild(opt);
      }
      dyn.textContent=css;
    }).catch(function(){});
  })();

  // Groups (extended)
  var GROUPS = {
    abilities: ['Strength','Agility','Stamina','Intelligence','Sense','Psychic','BodyDev','NanoPool'],
    melee: ['OneHandedBlunt','TwoHandedBlunt','OneHandedEdged','TwoHandedEdged','Piercing','MeleeEnergy','MartialArts','MultiMelee','FastAttack','Brawl','SneakAttack','Dimach'],
    ranged: ['Pistol','SubMachineGun','AssaultRifle','Shotgun','Rifle','RangedEnergy','Grenade','HeavyWeapons','Bow','BowSpecialAttack','MultiRanged','AimedShot','Burst','FlingShot','FullAuto'],
    nano: ['MatterCreation','MatterMetamorphosis','BiologicalMetamorphosis','TimeAndSpace','PsychologicalModification','SensoryImprovement','NanoCInit'],
    speeddef: ['MeleeInit','RangedInit','PhysicalInit','RunSpeed','DodgeRanged','EvadeClsC','DuckExp','NanoResist','HealDelta','NanoDelta','Treatment','FirstAid','AddAllDef','AddAllOff','CriticalIncrease'],
    trades: ['MechanicalEngineering','ElectricalEngineering','FieldQuantumPhysics','WeaponSmithing','Pharmaceuticals','Chemistry','ComputerLiteracy','Psychology','NanoProg','Tutoring'],
    spynav: ['Concealment','Perception','BreakingAndEntering','TrapDisarm','MapNavigation','VehicleAir','VehicleGround','VehicleWater']
  };

  function labelFor(n){
    var map={'TwoHandedEdged':'2HE','OneHandedEdged':'1HE','TwoHandedBlunt':'2HB','OneHandedBlunt':'1HB','MeleeEnergy':'ME','RangedEnergy':'RE','FullAuto':'Full Auto','FlingShot':'Fling','AimedShot':'Aimed Shot','ComputerLiteracy':'Comp Lit','NanoCInit':'Nano Init','NanoProg':'Nano Programming','BodyDev':'Body Dev','DuckExp':'Duck-Exp','DodgeRanged':'Dodge Ranged','EvadeClsC':'Evade Close','AddAllOff':'AAO','AddAllDef':'AAD','CriticalIncrease':'Crit+','XPModifier':'XP %','MatterCreation':'MC','MatterMetamorphosis':'MM','BiologicalMetamorphosis':'BM','TimeAndSpace':'TS','PsychologicalModification':'PM','SensoryImprovement':'SI','SubMachineGun':'SMG','NanoResist':'Nano Resist','FirstAid':'First Aid','HealDelta':'Heal Delta','NanoDelta':'Nano Delta','Treatment':'Treatment'};
    if (map[n]) return map[n];
    return n.replace(/([a-z])([A-Z])/g,'$1 $2');
  }

  var serverHidden = {}; // name -> 1 (from config)
  function makeItem(name, val, withCloser){
    var div=document.createElement('div'); div.className='kv'; div.dataset.name=name;
    var left=document.createElement('span'); left.className='name'; left.textContent=labelFor(name);
    var right=document.createElement('span'); right.className='val numeric'; right.textContent=val|0;
    div.appendChild(left); div.appendChild(right);
    if (withCloser){
      var x=document.createElement('span'); x.className='closer'; x.textContent='×'; x.title='Hide';
      x.addEventListener('click', function(ev){ ev.stopPropagation(); post('misc_hide_add', name); });
      div.appendChild(x);
    }
    div.addEventListener('click', function(){
      var n=this.dataset.name; var pinned=this.classList.contains('pinned');
      if (pinned){ post('pin_remove', n); this.classList.remove('pinned'); }
      else { post('pin_add', n); this.classList.add('pinned'); }
    });
    return div;
  }

  function buildGroups(allNames, allMap){
    var assigned = {}; Object.keys(GROUPS).forEach(function(g){ GROUPS[g].forEach(function(n){ assigned[n]=1; }); });
    var misc = []; for (var i=0;i<allNames.length;i++){ var n=allNames[i]; if (!assigned[n]) misc.push(n); }

    function fill(groupKey, names, withCloser){
      var host = els.lists[groupKey]; host.innerHTML='';
      var frag=document.createDocumentFragment();
      names.forEach(function(n){ if (allMap[n]==null) return; frag.appendChild(makeItem(n, allMap[n], withCloser)); });
      host.appendChild(frag);
    }

    fill('abilities', GROUPS.abilities.filter(function(n){return allMap[n]!=null;}), false);
    fill('melee',     GROUPS.melee.filter(function(n){return allMap[n]!=null;}),     false);
    fill('ranged',    GROUPS.ranged.filter(function(n){return allMap[n]!=null;}),    false);
    fill('nano',      GROUPS.nano.filter(function(n){return allMap[n]!=null;}),      false);
    fill('speeddef',  GROUPS.speeddef.filter(function(n){return allMap[n]!=null;}),  false);
    fill('trades',    GROUPS.trades.filter(function(n){return allMap[n]!=null;}),    false);
    fill('spynav',    GROUPS.spynav.filter(function(n){return allMap[n]!=null;}),    false);

    // Misc: only here we allow hide/unhide
    var showHidden = !!els.showHidden && !!els.showHidden.checked;
    var host = els.lists.misc; host.innerHTML='';
    var frag=document.createDocumentFragment();
    misc.forEach(function(n){
      if (allMap[n]==null) return;
      var hidden = !!serverHidden[n];
      if (!showHidden && hidden) return;
      var div=makeItem(n, allMap[n], true);
      if (hidden){
        div.classList.add('hidden');
        var r=document.createElement('span'); r.className='closer'; r.textContent='↺'; r.title='Unhide';
        r.addEventListener('click', function(ev){ ev.stopPropagation(); post('misc_hide_remove', n); });
        div.appendChild(r);
      }
      frag.appendChild(div);
    });
    host.appendChild(frag);

    built=true;
    builtKey = allNames.join('|') + '|' + (showHidden ? '1':'0') + '|' + Object.keys(serverHidden).sort().join('|');
    applyFilter(true); // first pass after build
  }

  // Filter — open groups with matches, collapse groups without, keep state when cleared
  function applyFilter(fromBuild){
    var q=(els.filter.value||'').toLowerCase();

    Object.keys(els.lists).forEach(function(key){
      var list=els.lists[key];
      var items=list.children, anyShown=false;
      for (var i=0;i<items.length;i++){
        var n = items[i].dataset.name;
        var lab = labelFor(n).toLowerCase();
        var show = (q==='') || (n.toLowerCase().indexOf(q)>=0) || (lab.indexOf(q)>=0);
        items[i].style.display = show ? '' : 'none';
        if (show) anyShown = true;
      }
      if (q!==''){ var d=els.groups[key]; if (d) d.open = anyShown; }
    });
  }
  els.filter.addEventListener('input', function(){ applyFilter(false); });

  function connect(){
    if (es) es.close();
    es=new EventSource('/events');
    es.onopen=function(){ els.status.textContent='live'; };
    es.onerror=function(){ els.status.textContent='reconnecting…'; };
    es.onmessage=function(ev){ try{ render(JSON.parse(ev.data)); }catch(e){} };
  }

  function nz(v){ return v!=null && v!==0 && v!==12345678; }

  var lastPinVals = {}; // for delta blink
  function render(d){
    // settings
    if (d.settings){
      var s=d.settings;
      var allThemes='theme-dark theme-light theme-neon theme-synth theme-inferno theme-emerald theme-aurora theme-monokai';
      if (!document.body.classList.contains(s.theme)){
        document.body.classList.remove.apply(document.body.classList, allThemes.split(' '));
        document.body.classList.add(s.theme||'theme-neon');
      }
      if (els.theme.value!==s.theme) els.theme.value = s.theme;
      if (s.compact){ document.body.classList.add('compact'); } else { document.body.classList.remove('compact'); }
      els.compact.checked = !!s.compact;
      var im = s.interval_ms|0;
      if ((els.interval.value|0)!==im) els.interval.value = im;
      syncRateButtons(im);
      els.toggle.checked = !!s.enabled;
    }

    // core
    var c=d.core||{};
    els.aao.textContent = nz(c.AddAllOff) ? c.AddAllOff : '–';
    els.aad.textContent = nz(c.AddAllDef) ? c.AddAllDef : '–';
    els.crit.textContent= nz(c.CriticalIncrease) ? c.CriticalIncrease : '–';
    els.xpmod.textContent = nz(c.XPModifier) ? (c.XPModifier+'%') : '–';

    // hp/nano
    var hp=d.hp||{now:0,max:0,pct:0}; els.hpbar.style.width=(hp.pct||0)+'%'; els.hptext.textContent=(hp.now||0)+' / '+(hp.max||0)+' ('+(hp.pct||0)+'%)';
    var np=d.nano||{now:0,max:0,pct:0}; els.nanobar.style.width=(np.pct||0)+'%'; els.nanotext.textContent=(np.now||0)+' / '+(np.max||0)+' ('+(np.pct||0)+'%)';

    // +dmg chips
    els.dmgchips.innerHTML=''; var dm=d.dmg||{}, keys=Object.keys(dm).sort();
    for (var i=0;i<keys.length;i++){ var k=keys[i], v=dm[k]|0; if (!nz(v)) continue; var chip=document.createElement('div'); chip.className='chip numeric'; chip.textContent=k.replace('DamageModifier','')+': '+v; els.dmgchips.appendChild(chip); }

    // ACs
    els.acs.innerHTML=''; var ac=d.ac||{}, ack=Object.keys(ac).sort();
    for (var j=0;j<ack.length;j++){ var k2=ack[j], v2=ac[k2]|0; if (!nz(v2)) continue; var row=document.createElement('div'); row.className='ac'; var b=document.createElement('b'); b.textContent=k2.replace('AC',''); var sp=document.createElement('span'); sp.className='numeric'; sp.textContent=v2; row.appendChild(b); row.appendChild(sp); els.acs.appendChild(row); }

    // Build groups as needed
    var all=d.all||{}, names=d.all_names||[];
    var hid = d.hiddenMisc||[]; serverHidden = {}; for (var h=0; h<hid.length; h++) serverHidden[hid[h]] = 1;
    var buildKey = names.join('|') + '|' + (els.showHidden && els.showHidden.checked ? '1':'0') + '|' + Object.keys(serverHidden).sort().join('|');
    if (!built || buildKey!==builtKey) buildGroups(names, all);

    // Update values in lists
    Object.keys(els.lists).forEach(function(k){
      var list=els.lists[k], items=list.children;
      for (var i=0;i<items.length;i++){
        var name=items[i].dataset.name;
        var v = all[name]|0;
        items[i].querySelector('.val').textContent = v;
      }
    });

    // Pins (with numeric pill and delta blink)
    var pins = d.pins||[];
    els.pins.innerHTML= pins.length ? '' : 'None pinned yet. Use the groups to pin.';
    var pinSet={}; for (var p=0;p<pins.length;p++){ if (nz(pins[p].v)) pinSet[pins[p].name]=1; }
    Object.keys(els.lists).forEach(function(k){
      var list=els.lists[k], items=list.children;
      for (var i=0;i<items.length;i++){
        var n=items[i].dataset.name;
        if (pinSet[n]) items[i].classList.add('pinned'); else items[i].classList.remove('pinned');
      }
    });
    for (var q=0;q<pins.length;q++){
      var pi=pins[q]; if (!nz(pi.v)) continue;
      var div=document.createElement('div'); div.className='pin';
      div.innerHTML='<b>'+pi.label+'</b> <span class=""val numeric"">'+(pi.v|0)+'</span> <span class=""x"" title=""Unpin"">×</span>';
      (function(name,el){ el.querySelector('.x').addEventListener('click', function(){ post('pin_remove', name); }); })(pi.name,div);
      if (lastPinVals.hasOwnProperty(pi.name) && (lastPinVals[pi.name]|0)!==(pi.v|0)){ div.classList.add('changed'); setTimeout(function(){ div.classList.remove('changed'); }, 450); }
      lastPinVals[pi.name] = pi.v|0;
      els.pins.appendChild(div);
    }

    // keep current filter effect on new data
    if ((els.filter.value||'')!=='') applyFilter(false);

    els.status.textContent='live';
  }

  connect();
})();
";

    // ================== Overlay (minimal) ==================
    private static readonly string _overlayHtml =
@"<!doctype html><html lang=""en""><head>
<meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>NotumHUD Overlay</title>
<link rel=""stylesheet"" href=""/app.css"">
<style>
  body{background:transparent}
  .overlay{position:fixed;top:24px;left:24px;min-width:320px;max-width:520px;border:1px solid var(--card-br);border-radius:14px;background:linear-gradient(180deg, rgba(255,255,255,.04), rgba(255,255,255,.02));padding:12px;box-shadow:0 8px 30px rgba(0,0,0,.35);cursor:move}
  .ov-title{font-weight:800;color:var(--accent);margin-bottom:8px;user-select:none}
  .ov-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}
  .ov-cell{border:1px solid var(--card-br);border-radius:10px;padding:8px;background:var(--card);text-align:center}
  .ov-cell .k{font-size:11px;opacity:.88;color:var(--muted);text-transform:uppercase;letter-spacing:.12em}
  .ov-cell .v{font-size:22px;font-weight:800}
  .ov-wide{grid-column:span 4}
  .ov-bar{height:12px;border:1px solid var(--card-br);border-radius:999px;overflow:hidden;background:rgba(255,255,255,.06)}
  .ov-fill{height:100%;background:linear-gradient(90deg,var(--bar1),var(--bar2))}
  .ov-sub{font-size:12px;opacity:.85;color:var(--muted);margin-top:4px;text-align:center}
</style>
</head>
<body>
<div class=""overlay"" id=""ov"">
  <div class=""ov-title"">NotumHUD Overlay</div>
  <div class=""ov-grid"">
    <div class=""ov-cell""><div class=""k"">AAO</div><div class=""v numeric"" id=""ov-aao"">–</div></div>
    <div class=""ov-cell""><div class=""k"">AAD</div><div class=""v numeric"" id=""ov-aad"">–</div></div>
    <div class=""ov-cell""><div class=""k"">Crit+</div><div class=""v numeric"" id=""ov-crit"">–</div></div>
    <div class=""ov-cell""><div class=""k"">XP%</div><div class=""v numeric"" id=""ov-xp"">–</div></div>

    <div class=""ov-cell ov-wide"">
      <div class=""k"">HP</div>
      <div class=""ov-bar""><div class=""ov-fill"" id=""ov-hpbar""></div></div>
      <div class=""ov-sub numeric"" id=""ov-hptext"">–</div>
    </div>
    <div class=""ov-cell ov-wide"">
      <div class=""k"">Nano</div>
      <div class=""ov-bar""><div class=""ov-fill"" id=""ov-nanobar""></div></div>
      <div class=""ov-sub numeric"" id=""ov-nanotext"">–</div>
    </div>
  </div>
</div>
<script src=""/overlay.js""></script>
</body></html>";

    private static readonly string _overlayJs =
@"(function(){
  var aao=document.getElementById('ov-aao'), aad=document.getElementById('ov-aad'), crit=document.getElementById('ov-crit'), xp=document.getElementById('ov-xp');
  var hpbar=document.getElementById('ov-hpbar'), hptext=document.getElementById('ov-hptext');
  var nb=document.getElementById('ov-nanobar'), nt=document.getElementById('ov-nanotext');

  // Drag
  (function(){
    var el=document.getElementById('ov'), ox=0, oy=0, down=false, sx=0, sy=0;
    el.addEventListener('mousedown', function(e){ down=true; sx=e.clientX; sy=e.clientY; var r=el.getBoundingClientRect(); ox=r.left; oy=r.top; e.preventDefault(); });
    window.addEventListener('mousemove', function(e){ if(!down) return; var dx=e.clientX-sx, dy=e.clientY-sy; el.style.left=(ox+dx)+'px'; el.style.top=(oy+dy)+'px'; });
    window.addEventListener('mouseup', function(){ down=false; });
  })();

  // Custom themes hook
  (function(){
    var dyn=document.createElement('style'); dyn.id='dyn-themes'; document.head.appendChild(dyn);
    fetch('/api/themes').then(function(r){return r.text();}).then(function(t){
      var arr=[]; try{ arr=JSON.parse(t||'[]'); }catch(e){}
      var css=''; for (var i=0;i<arr.length;i++){ var th=arr[i]; if (!th||!th.id||!th.vars) continue; var cls='body.'+th.id; css+=cls+'{'; for (var k in th.vars){ if(Object.prototype.hasOwnProperty.call(th.vars,k)){ css+=k+':'+th.vars[k]+';'; } } css+='}'; }
      dyn.textContent=css;
    }).catch(function(){});
  })();

  function nz(v){ return v!=null && v!==0 && v!==12345678; }
  var es=new EventSource('/events');
  es.onmessage=function(ev){
    try{
      var d=JSON.parse(ev.data||'{}');
      var c=d.core||{}, s=d.settings||{}, hp=d.hp||{now:0,max:0,pct:0}, np=d.nano||{now:0,max:0,pct:0};
      var allThemes='theme-dark theme-light theme-neon theme-synth theme-inferno theme-emerald theme-aurora theme-monokai';
      if (s.theme){ document.body.classList.remove.apply(document.body.classList, allThemes.split(' ')); document.body.classList.add(s.theme); }
      aao.textContent = nz(c.AddAllOff) ? c.AddAllOff : '–';
      aad.textContent = nz(c.AddAllDef) ? c.AddAllDef : '–';
      crit.textContent= nz(c.CriticalIncrease) ? c.CriticalIncrease : '–';
      xp.textContent  = nz(c.XPModifier) ? (c.XPModifier+'%') : '–';
      hpbar.style.width=(hp.pct||0)+'%'; hptext.textContent=(hp.now||0)+' / '+(hp.max||0)+' ('+(hp.pct||0)+'%)';
      nb.style.width=(np.pct||0)+'%';     nt.textContent =(np.now||0)+' / '+(np.max||0)+' ('+(np.pct||0)+'%)';
    }catch(e){}
  };
})();
";
}
