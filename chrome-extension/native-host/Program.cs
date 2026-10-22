using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LiveCaptionViewer.Models;
using LiveCaptionViewer.Services;

namespace LiveCaptionNativeHost;

internal static class Program
{
    internal const int HttpPort = 17891;
    private static readonly object Gate = new();
    private static readonly Stream Stdout = Console.OpenStandardOutput();
    private static readonly LiveCaptionsCaptureService Capture = new();
    private static string _latestText = "";
    private static string _status = "idle";
    private static string? _error;

    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveCaptionAssistant");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "host.log"), $"{DateTime.Now:o} {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the host */ }
    }

    private static int Main(string[] args)
    {
        bool standalone = args.Any(a => string.Equals(a, "--standalone", StringComparison.OrdinalIgnoreCase));
        Log(standalone ? "started (standalone HTTP)" : "started (Chrome native messaging)");
        Capture.CaptionUpdated += (_, caption) =>
        {
            lock (Gate) _latestText = caption.Text ?? "";
            SendNative(new { type = "caption", text = caption.Text ?? "" });
        };
        Capture.StatusChanged += (_, status) =>
        {
            lock (Gate)
            {
                _status = MapStatus(status);
                if (status != CaptureStatus.Error) _error = null;
            }
            SendNative(new { type = "status", status = MapStatus(status) });
        };
        Capture.ErrorOccurred += (_, ex) =>
        {
            lock (Gate)
            {
                _status = "error";
                _error = ex.Message;
            }
            SendNative(new { type = "status", status = "error", error = ex.Message });
        };

        _ = Task.Run(RunHttpServer);
        if (standalone)
            Thread.Sleep(Timeout.Infinite);
        else
            ReadNativeLoop();

        try { Capture.StopAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
        Capture.Dispose();
        return 0;
    }

    private static string MapStatus(CaptureStatus status) => status switch
    {
        CaptureStatus.Idle => "idle",
        CaptureStatus.SearchingForLiveCaptions => "searching",
        CaptureStatus.LiveCaptionsNotFound => "notfound",
        CaptureStatus.Monitoring => "monitoring",
        CaptureStatus.Stopped => "stopped",
        CaptureStatus.Error => "error",
        _ => "idle"
    };

    private static void HandleAction(string? action)
    {
        if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
            Capture.Start();
        else if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            _ = Capture.StopAsync();
    }

    private static object Snapshot()
    {
        lock (Gate)
        {
            return new { type = "snapshot", status = _status, text = _latestText, error = _error };
        }
    }

    private static void ReadNativeLoop()
    {
        using var stdin = Console.OpenStandardInput();
        var lenBuf = new byte[4];
        while (true)
        {
            if (!ReadExact(stdin, lenBuf, 4)) break;
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 10_000_000) break;
            var payload = new byte[len];
            if (!ReadExact(stdin, payload, len)) break;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("action", out var action))
                    HandleAction(action.GetString());
            }
            catch { /* ignore */ }
        }
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = stream.Read(buffer, offset, count - offset);
            if (n <= 0) return false;
            offset += n;
        }
        return true;
    }

    private static void SendNative(object message)
    {
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (Gate)
            {
                Stdout.Write(BitConverter.GetBytes(json.Length), 0, 4);
                Stdout.Write(json, 0, json.Length);
                Stdout.Flush();
            }
        }
        catch { /* Chrome closed the pipe */ }
    }

    private static async Task RunHttpServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, HttpPort);
        try
        {
            listener.Start();
            Log($"HTTP listening on 127.0.0.1:{HttpPort}");
        }
        catch (Exception ex)
        {
            Log($"HTTP listen failed: {ex.Message}");
            return;
        }

        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleTcpClient(client));
        }
    }

    private static void HandleTcpClient(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            stream.ReadTimeout = 3000;
            stream.WriteTimeout = 3000;
            try
            {
                var buf = new byte[8192];
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return;
                string raw = Encoding.ASCII.GetString(buf, 0, n);
                string first = raw.Split('\n')[0];
                string[] parts = first.Split(' ');
                string method = parts.Length > 0 ? parts[0].Trim() : "GET";
                string path = parts.Length > 1 ? parts[1].Trim() : "/";
                int q = path.IndexOf('?');
                // ?fresh=1 (sent by a hotkey): read the Live Captions window right now instead of returning the last polled text.
                bool fresh = q >= 0 && path[q..].Contains("fresh=1", StringComparison.OrdinalIgnoreCase);
                if (q >= 0) path = path[..q];
                path = path.TrimEnd('/').ToLowerInvariant();
                if (path.Length == 0) path = "/";

                if (method == "OPTIONS")
                {
                    WriteHttp(stream, 204, "text/plain", "");
                    return;
                }

                if (method == "POST" && path == "/start")
                {
                    HandleAction("start");
                    WriteJson(stream, Snapshot());
                    return;
                }
                if (method == "POST" && path == "/stop")
                {
                    HandleAction("stop");
                    WriteJson(stream, Snapshot());
                    return;
                }
                if (fresh && Capture.ReadNow() is { } now)
                    lock (Gate) _latestText = now;
                WriteJson(stream, Snapshot());
            }
            catch (Exception ex)
            {
                Log($"HTTP request failed: {ex.Message}");
            }
        }
    }

    private static void WriteJson(NetworkStream stream, object body)
    {
        WriteHttp(stream, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(body));
    }

    private static void WriteHttp(NetworkStream stream, int status, string contentType, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
        string reason = status == 204 ? "No Content" : "OK";
        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {status} {reason}\r\n");
        header.Append("Access-Control-Allow-Origin: *\r\n");
        header.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        header.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        header.Append($"Content-Type: {contentType}\r\n");
        header.Append($"Content-Length: {payload.Length}\r\n");
        header.Append("Connection: close\r\n\r\n");
        byte[] head = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(head, 0, head.Length);
        if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }
}
