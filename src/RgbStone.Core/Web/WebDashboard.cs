using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.Json;
using RgbStone.Core;

namespace RgbStone.Web
{
    /// <summary>
    /// Optional web dashboard for phones/other PCs on the home network.
    /// Off by default; nothing listens while it is off. A 4-digit PIN is required for every request that reads or changes anything,
    /// wrong PINs are rate-limited, and the Windows firewall rule it adds allows only the private (home) network profile.
    /// </summary>
    public sealed class WebDashboard : IDisposable
    {
        public const int Port = 8787;
        const string FirewallRule = "rgbstone web panosu";

        readonly Engine _engine;
        readonly AppSettings _settings;
        readonly HashSet<string> _tokens = new HashSet<string>();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        HttpListener _listener;
        Thread _thread;
        int _failures;
        DateTime _lockedUntil;

        public bool Running => _listener != null && _listener.IsListening;

        public WebDashboard(Engine engine, AppSettings settings)
        {
            _engine = engine;
            _settings = settings;
        }

        public static string NewPin()
        {
            var b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return (BitConverter.ToUInt32(b, 0) % 10000).ToString("0000");
        }

        /// <summary>Private IPv4 addresses of this PC, for showing "open http://… on your phone".</summary>
        public static List<string> LocalAddresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && !n.Name.StartsWith("vEthernet") && n.Description.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) < 0
                         && n.Description.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) < 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                .Select(a => $"http://{a}:{Port}")
                .Distinct().ToList();

        static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        public bool Start()
        {
            if (Running) return true;
            if (string.IsNullOrEmpty(_settings.WebPin)) { _settings.WebPin = NewPin(); _settings.Save(); }
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{Port}/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Web panosu başlatılamadı", ex);
                _listener = null;
                return false;
            }
            AddFirewallRule(_settings);
            _thread = new Thread(Loop) { IsBackground = true, Name = "rgb web" };
            _thread.Start();
            Log.Info("Web panosu açık: " + string.Join(", ", LocalAddresses()));
            return true;
        }

        public void Stop()
        {
            if (_listener == null) return;
            try { _listener.Stop(); _listener.Close(); } catch { }
            _listener = null;
            lock (_tokens) _tokens.Clear();
            RemoveFirewallRule(_settings);
            Log.Info("Web panosu kapatıldı.");
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Removes a rule left behind if rgbstone was killed while the dashboard was on. netsh only runs when the rule was
        /// added: a hidden elevated netsh on every start-up is the kind of thing behavioral antivirus scores as an attack.
        /// </summary>
        public static void CleanupIfDisabled(AppSettings s) { if (!s.WebEnabled && s.FirewallRuleAdded) RemoveFirewallRule(s); }

        // ---------- firewall: private (home) profile only, removed when the dashboard is turned off ----------

        static void AddFirewallRule(AppSettings s)
        {
            if (s.FirewallRuleAdded) return;
            Netsh($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={Port} profile=private");
            s.FirewallRuleAdded = true;
            s.Save();
        }

        static void RemoveFirewallRule(AppSettings s)
        {
            if (!s.FirewallRuleAdded) return;
            Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
            s.FirewallRuleAdded = false;
            s.Save();
        }

        static void Netsh(string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                }
            }
            catch (Exception ex) { Log.Error("Güvenlik duvarı kuralı", ex); }
        }

        // ---------- HTTP ----------

        void Loop()
        {
            var listener = _listener;
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var path = req.Url.AbsolutePath;
                if (!IsPrivate(req.RemoteEndPoint.Address) && !IPAddress.IsLoopback(req.RemoteEndPoint.Address))
                {
                    Send(ctx, 403, "text/plain", "yalnızca ev ağı");
                    return;
                }
                if (path == "/" && req.HttpMethod == "GET") { Send(ctx, 200, "text/html; charset=utf-8", Page()); return; }
                if (path == "/icon.png") { SendBytes(ctx, 200, "image/png", Resource("logo-64.png")); return; }
                if (path == "/api/login" && req.HttpMethod == "POST") { Login(ctx); return; }

                if (!Authorized(req)) { Send(ctx, 401, "application/json", "{\"error\":\"pin\"}"); return; }
                if (path == "/api/state" && req.HttpMethod == "GET") { Json(ctx, State()); return; }
                if (path == "/api/preview" && req.HttpMethod == "GET") { Json(ctx, Preview()); return; }
                if (path == "/api/set" && req.HttpMethod == "POST") { Update(ReadJson(req)); Json(ctx, State()); return; }
                Send(ctx, 404, "text/plain", "yok");
            }
            catch (Exception ex)
            {
                Log.Error("Web isteği", ex);
                try { Send(ctx, 500, "text/plain", "hata"); } catch { }
            }
        }

        void Login(HttpListenerContext ctx)
        {
            if (DateTime.UtcNow < _lockedUntil) { Send(ctx, 429, "application/json", "{\"error\":\"wait\"}"); return; }
            var body = ReadJson(ctx.Request);
            string pin = body.TryGetValue("pin", out var p) ? Convert.ToString(p) : "";
            if (pin != _settings.WebPin)
            {
                if (++_failures >= 5) { _lockedUntil = DateTime.UtcNow.AddMinutes(1); _failures = 0; }
                Log.Warn("Web panosu: yanlış PIN, " + ctx.Request.RemoteEndPoint.Address);
                Send(ctx, 401, "application/json", "{\"error\":\"pin\"}");
                return;
            }
            _failures = 0;
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            string token = BitConverter.ToString(b).Replace("-", "");
            lock (_tokens) _tokens.Add(token);
            ctx.Response.AppendHeader("Set-Cookie", $"rgbstone={token}; Path=/; HttpOnly; SameSite=Strict");
            Json(ctx, new Dictionary<string, object> { ["ok"] = true });
        }

        bool Authorized(HttpListenerRequest req)
        {
            var c = req.Cookies["rgbstone"];
            if (c == null) return false;
            lock (_tokens) return _tokens.Contains(c.Value);
        }

        object State() => new Dictionary<string, object>
        {
            ["effect"] = _settings.Effect,
            ["effects"] = Effect.All.Select(e => new Dictionary<string, object> { ["id"] = e.Id, ["name"] = e.DisplayName }).ToList(),
            ["color1"] = _settings.Color1,
            ["color2"] = _settings.Color2,
            ["speed"] = _settings.Speed,
            ["brightness"] = _settings.Brightness,
            ["fps"] = Math.Round(_engine.MeasuredFps),
            ["devices"] = _engine.Devices.ToList().Select(d => new Dictionary<string, object>
            {
                ["id"] = d.Id, ["name"] = d.Name, ["kind"] = d.Kind.ToString(), ["leds"] = d.TotalLeds,
                ["status"] = d.Status, ["enabled"] = _settings.For(d.Id).Enabled, ["paused"] = d.Paused,
            }).ToList(),
        };

        object Preview()
        {
            var fx = Effect.ById(_settings.Effect);
            var s = _settings.ToEffectSettings();
            double t = _clock.Elapsed.TotalSeconds;
            return Enumerable.Range(0, 48).Select(i => fx.ColorAtLed(i, i / 47.0, t, s).Scale(s.Brightness).ToString()).ToList();
        }

        void Update(Dictionary<string, object> b)
        {
            if (b.TryGetValue("effect", out var e) && Effect.All.Any(x => x.Id == Convert.ToString(e))) _settings.Effect = Convert.ToString(e);
            if (b.TryGetValue("color1", out var c1) && IsHex(c1)) _settings.Color1 = Convert.ToString(c1).ToUpperInvariant();
            if (b.TryGetValue("color2", out var c2) && IsHex(c2)) _settings.Color2 = Convert.ToString(c2).ToUpperInvariant();
            if (b.TryGetValue("speed", out var sp)) _settings.Speed = Math.Max(0.1, Math.Min(5, Convert.ToDouble(sp)));
            if (b.TryGetValue("brightness", out var br)) _settings.Brightness = Math.Max(0, Math.Min(100, Convert.ToInt32(br)));
            if (b.TryGetValue("device", out var dv) && b.TryGetValue("enabled", out var en))
            {
                string id = Convert.ToString(dv);
                if (_engine.Devices.Any(d => d.Id == id)) _settings.For(id).Enabled = Convert.ToBoolean(en);
            }
            _settings.Save();
            SettingsChanged?.Invoke();
        }

        /// <summary>Raised after the dashboard changed a setting, so the desktop window can refresh.</summary>
        public event Action SettingsChanged;

        static bool IsHex(object o)
        {
            var s = Convert.ToString(o);
            return s != null && s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
        }

        Dictionary<string, object> ReadJson(HttpListenerRequest req)
        {
            if (req.ContentLength64 > 4096) return new Dictionary<string, object>();
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                string body = r.ReadToEnd();
                try
                {
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var result = new Dictionary<string, object>();
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                        // Plain CLR values, so the Convert.ToX calls below work as they did with JavaScriptSerializer.
                        foreach (var p in doc.RootElement.EnumerateObject())
                            result[p.Name] = p.Value.ValueKind switch
                            {
                                JsonValueKind.String => p.Value.GetString(),
                                JsonValueKind.Number => p.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Null => null,
                                _ => p.Value.GetRawText(),
                            };
                        return result;
                    }
                }
                catch { return new Dictionary<string, object>(); }
            }
        }

        void Json(HttpListenerContext ctx, object o) => Send(ctx, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(o));

        static void Send(HttpListenerContext ctx, int code, string type, string text) => SendBytes(ctx, code, type, Encoding.UTF8.GetBytes(text));

        static void SendBytes(HttpListenerContext ctx, int code, string type, byte[] data)
        {
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = type;
            ctx.Response.AppendHeader("Cache-Control", "no-store");
            ctx.Response.AppendHeader("X-Content-Type-Options", "nosniff");
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }

        static byte[] Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return new byte[0];
                var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
            }
        }

        static string _page;
        static string Page() => _page ?? (_page = Encoding.UTF8.GetString(Resource("dashboard.html")));
    }
}
