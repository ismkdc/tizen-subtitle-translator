using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

/*
 * SubtitleTV root helper (the TV's /usr/bin/node refuses unsigned JS, so the
 * helper is a plain .NET console app like the runner).
 *
 * Started once by Vetro's /exec as root. Listens on 127.0.0.1:18765 and gives
 * the WGT UI a small CORS-enabled API:
 *
 *   GET  /health   GET /state   GET /config
 *   POST /config   POST /start  POST /stop
 */

internal static class Helper
{
    private const int Port = 18765;
    private const string Base = "/opt/usr/subtitle-ocr";
    private const string Probe = "/tmp/subtitle-probe";
    private const string ConfigPath = Base + "/config.json";
    private const string StatusPath = Probe + "/status.json";
    private const string ScenePath = Probe + "/live-scene.json";
    private const string RunnerPath = Base + "/SubtitleRunner.dll";
    private const string LogPath = Probe + "/runner.log";

    private static readonly object Gate = new object();

    internal sealed class Region
    {
        public double x { get; set; } = 0.208;
        public double y { get; set; } = 0.861;
        public double w { get; set; } = 0.583;
        public double h { get; set; } = 0.107;
    }

    internal sealed class Ocr
    {
        public int threshold { get; set; } = 155;
        public int minConfidence { get; set; } = 72;
    }

    internal sealed class Style
    {
        public int size { get; set; } = 34;
        public int bottom { get; set; } = 1000;
        public double opacity { get; set; } = 1;
        public string color { get; set; } = "#FFFFFF";
    }

    internal sealed class Translate
    {
        public string source { get; set; } = "EN";
        public string target { get; set; } = "TR";
    }

    internal sealed class Config
    {
        public Region region { get; set; } = new Region();
        public Ocr ocr { get; set; } = new Ocr();
        public Style style { get; set; } = new Style();
        public Translate translate { get; set; } = new Translate();
    }

    private static Config Defaults() { return new Config(); }

    private static Config Normalize(Config c)
    {
        if (c == null) c = Defaults();
        if (c.region == null) c.region = new Region();
        if (c.ocr == null) c.ocr = new Ocr();
        if (c.style == null) c.style = new Style();
        if (c.translate == null) c.translate = new Translate();

        c.region.w = Clamp(c.region.w, 0.05, 1);
        c.region.h = Clamp(c.region.h, 0.03, 0.8);
        c.region.x = Clamp(c.region.x, 0, 1 - c.region.w);
        c.region.y = Clamp(c.region.y, 0, 1 - c.region.h);
        c.ocr.threshold = (int)Math.Round(Clamp(c.ocr.threshold, 80, 240));
        c.ocr.minConfidence = (int)Math.Round(Clamp(c.ocr.minConfidence, 20, 99));
        c.style.size = (int)Math.Round(Clamp(c.style.size, 20, 64));
        c.style.bottom = (int)Math.Round(Clamp(c.style.bottom, 60, 1040));
        c.style.opacity = Clamp(c.style.opacity, 0.2, 1);
        if (string.IsNullOrEmpty(c.style.color) || !System.Text.RegularExpressions.Regex.IsMatch(c.style.color, "^#[0-9A-Fa-f]{6}$"))
            c.style.color = "#FFFFFF";
        else
            c.style.color = c.style.color.ToUpperInvariant();
        if (string.IsNullOrEmpty(c.translate.source)) c.translate.source = "EN";
        if (string.IsNullOrEmpty(c.translate.target)) c.translate.target = "TR";
        return c;
    }

    private static double Clamp(double v, double a, double b)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return a;
        return Math.Max(a, Math.Min(b, v));
    }

    private static Config ReadConfig()
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), opts);
            return Normalize(cfg);
        }
        catch { return Defaults(); }
    }

    private static void WriteConfig(Config c)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        string tmp = ConfigPath + ".new";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, opts) + "\n");
        File.Move(tmp, ConfigPath, true);
    }

    private static JsonElement? ReadStatus()
    {
        try
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(StatusPath)))
                return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static void WriteStatus(bool running)
    {
        try
        {
            var obj = new
            {
                running = running,
                updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                capture = (object)null,
                last = new { ocr = "", tr = "", ocr_ms = 0, tr_ms = 0 },
                error = (string)null
            };
            string tmp = StatusPath + ".new";
            File.WriteAllText(tmp, JsonSerializer.Serialize(obj));
            File.Move(tmp, StatusPath, true);
        }
        catch { }
    }

    private static string Exec(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return output;
            }
        }
        catch { return ""; }
    }

    private static string Quote(string s) { return "'" + s.Replace("'", "'\\''") + "'"; }

    private static bool Running()
    {
        string outp = Exec("pgrep -f 'tzcapturesample /opt/usr/subtitle-ocr/SubtitleRunner[.]dll' 2>/dev/null || true");
        foreach (var line in outp.Split('\n'))
        {
            int pid;
            if (int.TryParse(line.Trim(), out pid) && pid > 0) return true;
        }
        return false;
    }

    private static void StopPipeline()
    {
        Exec("pkill -9 -f 'tzcapturesample /opt/usr/subtitle-ocr/SubtitleRunner[.]dll' 2>/dev/null || true");
        Exec("pkill -9 -f 'dotnet /opt/usr/subtitle-ocr/OverlayProbe[.]dll' 2>/dev/null || true");
        try { File.WriteAllText(ScenePath, ""); } catch { }
    }

    private static void StartPipeline()
    {
        lock (Gate)
        {
            StopPipeline();
            Directory.CreateDirectory(Probe);
            try { File.WriteAllText(ScenePath, ""); } catch { }

            var cfg = ReadConfig();
            WriteConfig(cfg);

            string inner = "mount --make-rprivate / && " +
                "mount --bind " + Base + " /run/subtitle-ocr && " +
                "mount -o remount,bind,ro /run/subtitle-ocr && " +
                 "exec /usr/bin/bash -c \"exec -a /usr/bin/tzcapturesample /usr/bin/dotnet " + RunnerPath + " 0\"";
            string cmd = "unshare -m /bin/sh -c " + Quote(inner) +
                " >>" + LogPath + " 2>&1 </dev/null &";

            try
            {
                var psi = new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(cmd);
                Process.Start(psi);
            }
            catch { }

            WriteStatus(true);
        }
    }

    private static object State()
    {
        var cfg = ReadConfig();
        bool running = Running();
        var st = ReadStatus();
        object last = null;
        object capture = null;
        string error = null;
        if (st.HasValue)
        {
            var root = st.Value;
            JsonElement e;
            if (root.TryGetProperty("last", out e)) last = e;
            if (root.TryGetProperty("capture", out e) && e.ValueKind == JsonValueKind.Object) capture = e;
            if (root.TryGetProperty("error", out e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
        }
        bool key = false;
        try { key = new FileInfo(Base + "/deepl-key").Length > 0; } catch { key = false; }

        return new
        {
            ok = true,
            running = running,
            config = cfg,
            last = last,
            capture = capture,
            error = error,
            key = key,
            updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private static void Send(NetworkStream stream, int code, object payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        string head = "HTTP/1.1 " + code + " " + (code == 200 ? "OK" : code == 404 ? "Not Found" : "Error") + "\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static void SendOptions(NetworkStream stream)
    {
        string head = "HTTP/1.1 204 No Content\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Max-Age: 86400\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";
        byte[] b = Encoding.ASCII.GetBytes(head);
        stream.Write(b, 0, b.Length);
        stream.Flush();
    }

    private static byte[] ReadRequest(NetworkStream stream, out int headerLength)
    {
        headerLength = 0;
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int sep = -1;
        while (true)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
            var arr = buffer.ToArray();
            for (int i = 0; i + 3 < arr.Length; i++)
            {
                if (arr[i] == 13 && arr[i + 1] == 10 && arr[i + 2] == 13 && arr[i + 3] == 10)
                {
                    sep = i + 4;
                    break;
                }
            }
            if (sep > 0) break;
            if (buffer.Length > 262144) break;
        }
        var data = buffer.ToArray();
        if (sep < 0) sep = data.Length;
        headerLength = sep;
        return data;
    }

    private static void Handle(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = 10000;
            var stream = client.GetStream();
            int headerLength;
            byte[] raw = ReadRequest(stream, out headerLength);
            if (raw.Length == 0) return;

            string head = Encoding.ASCII.GetString(raw, 0, Math.Min(headerLength, raw.Length));
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return;
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0];
            string path = first[1].Split('?')[0];

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(lines[i].Substring(15).Trim(), out contentLength);
            }
            string body = "";
            if (contentLength > 0)
            {
                var bodyBytes = new byte[contentLength];
                int already = Math.Max(0, raw.Length - headerLength);
                int copied = Math.Min(already, contentLength);
                if (copied > 0) Array.Copy(raw, headerLength, bodyBytes, 0, copied);
                int need = contentLength - copied;
                int off = copied;
                while (need > 0)
                {
                    int n = stream.Read(bodyBytes, off, need);
                    if (n <= 0) break;
                    off += n; need -= n;
                }
                body = Encoding.UTF8.GetString(bodyBytes, 0, off);
            }

            if (method == "OPTIONS") { SendOptions(stream); return; }

            if (method == "GET" && path == "/health")
            {
                Send(stream, 200, new { ok = true, service = "subtitle-helper", version = 1 });
                return;
            }
            if (method == "GET" && path == "/state") { Send(stream, 200, State()); return; }
            if (method == "GET" && path == "/config") { Send(stream, 200, new { ok = true, config = ReadConfig() }); return; }

            if (method == "POST" && path == "/config")
            {
                try
                {
                    using (var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body))
                    {
                        JsonElement cfgEl;
                        Config cfg = Defaults();
                        if (doc.RootElement.TryGetProperty("config", out cfgEl))
                        {
                            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            cfg = JsonSerializer.Deserialize<Config>(cfgEl.GetRawText(), opts);
                        }
                        cfg = Normalize(cfg);
                        WriteConfig(cfg);
                        Send(stream, 200, new { ok = true, config = cfg });
                    }
                }
                catch (Exception e)
                {
                    Send(stream, 400, new { ok = false, error = "bad config: " + e.Message });
                }
                return;
            }

            if (method == "POST" && path == "/start")
            {
                StartPipeline();
                Thread.Sleep(1200);
                Send(stream, 200, State());
                return;
            }

            if (method == "POST" && path == "/stop")
            {
                lock (Gate)
                {
                    StopPipeline();
                    WriteStatus(false);
                }
                Send(stream, 200, State());
                return;
            }

            Send(stream, 404, new { ok = false, error = "not found" });
        }
    }

    private static int Main(string[] args)
    {
        try { Directory.CreateDirectory(Probe); } catch { }
        try { File.WriteAllText("/tmp/subtitle-helper.pid", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)); } catch { }
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        Console.WriteLine("subtitle-helper listening on 127.0.0.1:" + Port);
        while (true)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(delegate { try { Handle(client); } catch { try { client.Close(); } catch { } } });
            }
            catch (Exception e)
            {
                Console.WriteLine("accept_error=" + e.Message);
                Thread.Sleep(200);
            }
        }
    }
}
