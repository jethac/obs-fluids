using System.Net;
using System.Text;
using System.Text.Json;
using HidSharp;

namespace InkContainer;

static class EnergyHub
{
    public const int Port = 17331;
    public static event Action<string>? Message;
    static CancellationTokenSource? _cts;

    public static void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ListenHttp(token), token);
        _ = Task.Run(() => ListenHid(token), token);
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    public static void PublishDelta(int ticks, string src)
    {
        if (ticks == 0) return;
        var json = JsonSerializer.Serialize(new { delta = ticks, src });
        Message?.Invoke(json);
    }

    public static void PublishValue(float value, string src)
    {
        var json = JsonSerializer.Serialize(new { energy = Math.Clamp(value, 0f, 1f), src });
        Message?.Invoke(json);
    }

    static async Task ListenHttp(CancellationToken token)
    {
        HttpListener? listener = null;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
        }
        catch
        {
            return;
        }

        using (listener)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(token); }
                catch (OperationCanceledException) { break; }
                catch { continue; }
                _ = Task.Run(() => Handle(ctx), token);
            }
        }
    }

    static async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "content-type";
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            if (req.Url?.AbsolutePath.TrimEnd('/') == "/energy")
            {
                if (req.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("delta", out var d))
                        PublishDelta(d.GetInt32(), "http");
                    if (root.TryGetProperty("energy", out var e) || root.TryGetProperty("value", out e))
                        PublishValue(e.GetSingle(), "http");
                    var q = req.QueryString;
                    if (int.TryParse(q["d"], out var qd)) PublishDelta(qd, "http");
                    if (float.TryParse(q["v"], out var qv)) PublishValue(qv, "http");
                }
                res.StatusCode = 200;
                var payload = "{\"ok\":true}"u8.ToArray();
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(payload);
                res.Close();
                return;
            }

            res.StatusCode = 404;
            res.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    static async Task ListenHid(CancellationToken token)
    {
        var seen = new HashSet<string>();
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var dev in DeviceList.Local.GetHidDevices(vendorID: 0x046d))
                {
                    int pid = dev.ProductID;
                    if (pid is not (0xbc00 or 0xc354)) continue;
                    var key = dev.DevicePath;
                    if (!seen.Add(key)) continue;
                    _ = Task.Run(() => ReadDevice(dev, token), token);
                }
            }
            catch { }
            try { await Task.Delay(2000, token); } catch { break; }
        }
    }

    static void ReadDevice(HidDevice dev, CancellationToken token)
    {
        HidStream? stream = null;
        try
        {
            if (!dev.TryOpen(out stream)) return;
            stream.ReadTimeout = 250;
            var len = Math.Max(8, dev.GetMaxInputReportLength());
            var buf = new byte[len];
            byte[]? prev = null;
            while (!token.IsCancellationRequested)
            {
                int n;
                try { n = stream.Read(buf); }
                catch (TimeoutException) { continue; }
                catch { break; }
                if (n <= 0) continue;
                var cur = buf.AsSpan(0, n).ToArray();
                if (prev == null) { prev = cur; continue; }
                var ticks = RelDelta(prev, cur);
                prev = cur;
                if (ticks != 0) PublishDelta(ticks, "mx");
            }
        }
        catch { }
        finally { stream?.Dispose(); }
    }

    static int RelDelta(byte[] prev, byte[] cur)
    {
        int best = 0;
        var n = Math.Min(prev.Length, cur.Length);
        for (var i = 0; i < n; i++)
        {
            var d = (int)cur[i] - prev[i];
            if (d > 127) d -= 256;
            if (d < -128) d += 256;
            if (d != 0 && Math.Abs(d) <= 24 && Math.Abs(d) > Math.Abs(best))
                best = d;
        }
        return best;
    }
}
