using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeroStub;

// Automates TikTok Google account creation via Chrome DevTools Protocol.
// No HVNC needed — Chrome runs off-screen, fully automated.
// Uses a minimal TCP WebSocket client (no System.Net.WebSockets dependency).
internal static class TikTokCdpFeature
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static int _msgId;

    // ── Entry point ─────────────────────────────────────────────────────────

    public static async Task<(bool ok, string account, string cookie, string error)> RunAsync(
        Func<string, Task> onStatus, CancellationToken ct)
    {
        await onStatus("Searching for Chrome...");
        var chromePath = FindChrome();
        if (chromePath == null) return (false, "", "", "Chrome not found");

        await onStatus("Finding Google profile...");
        var profile = FindGoogleProfile();

        var port = GetFreePort();

        await onStatus($"Launching Chrome (port {port})...");
        var args = $"--remote-debugging-port={port} " +
                   "--no-first-run --no-default-browser-check --disable-extensions " +
                   "--window-position=-8000,-8000 --window-size=1280,800 " +
                   "--disable-blink-features=AutomationControlled " +
                   "--disable-features=IsolateOrigins " +
                   (profile != null ? $"--user-data-dir=\"{profile}\" " : "") +
                   "about:blank";

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName        = chromePath,
            Arguments       = args,
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
        if (proc == null) return (false, "", "", "Failed to start Chrome");

        try
        {
            // Wait for CDP
            await onStatus("Waiting for Chrome CDP...");
            string? pageWs = null;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500, ct);
                try
                {
                    var json = await _http.GetStringAsync($"http://localhost:{port}/json", ct);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var t in doc.RootElement.EnumerateArray())
                    {
                        if (t.TryGetProperty("type", out var tp) && tp.GetString() == "page" &&
                            t.TryGetProperty("webSocketDebuggerUrl", out var wu))
                        { pageWs = wu.GetString(); break; }
                    }
                    if (pageWs != null) break;
                }
                catch { }
            }
            if (pageWs == null) return (false, "", "", "Chrome CDP not ready");

            // Connect minimal WebSocket to page target
            var ws = new CdpWs();
            await ws.ConnectAsync(pageWs, ct);

            await ws.SendAsync("Page.enable",    "{}", ct);
            await ws.SendAsync("Network.enable", "{}", ct);
            await ws.SendAsync("Target.setDiscoverTargets", "{\"discover\":true}", ct);

            // Check for existing TikTok session before attempting signup
            await onStatus("Checking for existing TikTok session...");
            await ws.SendAsync("Page.navigate", "{\"url\":\"https://www.tiktok.com\"}", ct);
            await ws.WaitForLoadAsync(12000, ct);
            await Task.Delay(2000, ct);

            var existingCookieJson = await ws.CallAsync("Network.getCookies",
                "{\"urls\":[\"https://www.tiktok.com\"]}", ct);
            var existingCookie = ExtractTikTokSession(existingCookieJson);

            if (!string.IsNullOrEmpty(existingCookie))
            {
                await onStatus("Existing TikTok session found — skipping signup.");
                var existingUsername = await ws.EvalAsync(
                    "document.querySelector('[data-e2e=\"user-title\"]')?.textContent" +
                    "||document.querySelector('[class*=\"user-name\"]')?.textContent" +
                    "||document.querySelector('[class*=\"username\"]')?.textContent||''", ct);
                ws.Dispose();
                return (true, existingUsername ?? "", existingCookie, "");
            }

            // No existing session — proceed with signup
            await onStatus("No existing session — navigating to TikTok signup...");
            await ws.SendAsync("Page.navigate", "{\"url\":\"https://www.tiktok.com/signup\"}", ct);
            await ws.WaitForLoadAsync(12000, ct);
            await Task.Delay(2500, ct);

            // Click "Continue with Google"
            await onStatus("Clicking Continue with Google...");
            const string clickGoogleJs =
                "(function(){" +
                "var btn=document.querySelector('[data-e2e=\"sns-login-google\"]')" +
                "||document.querySelector('[aria-label*=\"Google\"]')" +
                "||Array.from(document.querySelectorAll('div[role=button],button')).find(e=>e.textContent.includes('Google'));" +
                "if(btn){btn.click();return 'clicked';}return 'not_found';})()";
            var clickResult = await ws.EvalAsync(clickGoogleJs, ct);
            if (clickResult != "clicked")
                await ws.EvalAsync("document.querySelector('a[href*=\"google\"]')?.click()", ct);

            // Wait for Google OAuth popup
            await onStatus("Waiting for Google OAuth popup...");
            string? popupWs = await WaitForPopupAsync(port, 20000, ct);

            if (popupWs != null)
            {
                await onStatus("Confirming Google sign-in...");
                var popup = new CdpWs();
                await popup.ConnectAsync(popupWs, ct);
                await popup.SendAsync("Page.enable", "{}", ct);
                await popup.WaitForLoadAsync(10000, ct);
                await Task.Delay(1500, ct);
                const string confirmJs =
                    "(function(){" +
                    "var btn=document.getElementById('submit_approve_access')" +
                    "||document.querySelector('[id*=submit]')" +
                    "||document.querySelector('button[type=submit]');" +
                    "if(btn){btn.click();return 'ok';}return 'auto';})()";
                await popup.EvalAsync(confirmJs, ct);
                popup.Dispose();
            }

            // Wait for account creation
            await onStatus("Waiting for account creation...");
            await ws.WaitForLoadAsync(15000, ct);
            await Task.Delay(3000, ct);

            // Extract cookies and username
            var cookieJson = await ws.CallAsync("Network.getCookies",
                "{\"urls\":[\"https://www.tiktok.com\"]}", ct);
            var cookie = ExtractTikTokSession(cookieJson);

            var usernameJs =
                "document.querySelector('[data-e2e=\"user-title\"]')?.textContent" +
                "||document.querySelector('[class*=\"user-name\"]')?.textContent" +
                "||document.querySelector('[class*=\"username\"]')?.textContent||''";
            var username = await ws.EvalAsync(usernameJs, ct);
            ws.Dispose();

            if (!string.IsNullOrEmpty(cookie) || !string.IsNullOrEmpty(username))
                return (true, username ?? "", cookie, "");

            return (false, "", "", "Signup may be pending verification");
        }
        catch (Exception ex) { return (false, "", "", ex.Message); }
        finally { try { proc.Kill(entireProcessTree: true); } catch { } }
    }

    // ── Minimal TCP WebSocket client (no System.Net.WebSockets) ─────────────

    private sealed class CdpWs : IDisposable
    {
        private TcpClient? _tcp;
        private NetworkStream? _ns;
        private readonly byte[] _rbuf = new byte[131072];

        public async Task ConnectAsync(string wsUrl, CancellationToken ct)
        {
            // Parse ws://host:port/path
            var uri  = new Uri(wsUrl);
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(uri.Host, uri.Port, ct);
            _ns = _tcp.GetStream();

            // HTTP upgrade
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var req = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                      $"Host: {uri.Host}:{uri.Port}\r\n" +
                      "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {key}\r\n" +
                      "Sec-WebSocket-Version: 13\r\n\r\n";
            var reqBytes = Encoding.ASCII.GetBytes(req);
            await _ns.WriteAsync(reqBytes, ct);

            // Read until end of headers
            int total = 0;
            while (total < 4 || !EndsWithCrLfCrLf(_rbuf, total))
            {
                int n = await _ns.ReadAsync(_rbuf.AsMemory(total, 1), ct);
                if (n == 0) throw new Exception("WS handshake failed");
                total += n;
            }
        }

        private static bool EndsWithCrLfCrLf(byte[] buf, int len) =>
            len >= 4 && buf[len-4] == '\r' && buf[len-3] == '\n' &&
                        buf[len-2] == '\r' && buf[len-1] == '\n';

        public async Task SendAsync(string method, string paramsJson, CancellationToken ct)
        {
            var id  = System.Threading.Interlocked.Increment(ref _msgId);
            var msg = $"{{\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";
            await WriteFrameAsync(Encoding.UTF8.GetBytes(msg), ct);
        }

        public async Task<string> CallAsync(string method, string paramsJson, CancellationToken ct)
        {
            var id  = System.Threading.Interlocked.Increment(ref _msgId);
            var msg = $"{{\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";
            await WriteFrameAsync(Encoding.UTF8.GetBytes(msg), ct);

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(8000);
            while (!cts2.Token.IsCancellationRequested)
            {
                var text = await ReadFrameAsync(cts2.Token);
                if (text.Contains($"\"id\":{id}")) return text;
            }
            return "";
        }

        public async Task<string?> EvalAsync(string expression, CancellationToken ct)
        {
            var escaped = expression.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var result  = await CallAsync("Runtime.evaluate",
                $"{{\"expression\":\"{escaped}\",\"returnByValue\":true}}", ct);
            try
            {
                using var doc = JsonDocument.Parse(result);
                if (doc.RootElement.TryGetProperty("result", out var r) &&
                    r.TryGetProperty("result", out var rv) &&
                    rv.TryGetProperty("value", out var v))
                    return v.GetString();
            }
            catch { }
            return null;
        }

        public async Task WaitForLoadAsync(int timeoutMs, CancellationToken ct)
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(timeoutMs);
            try
            {
                while (!cts2.Token.IsCancellationRequested)
                {
                    var text = await ReadFrameAsync(cts2.Token);
                    if (text.Contains("loadEventFired") || text.Contains("DOMContentLoaded")) return;
                }
            }
            catch (OperationCanceledException) { }
        }

        // WebSocket frame writer (client→server, with masking)
        private async Task WriteFrameAsync(byte[] payload, CancellationToken ct)
        {
            var mask = RandomNumberGenerator.GetBytes(4);
            var masked = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
                masked[i] = (byte)(payload[i] ^ mask[i % 4]);

            var header = BuildFrameHeader(payload.Length, mask);
            await _ns!.WriteAsync(header, ct);
            await _ns.WriteAsync(masked, ct);
        }

        private static byte[] BuildFrameHeader(int length, byte[] mask)
        {
            byte[] h;
            if (length <= 125)
            {
                h = new byte[6];
                h[1] = (byte)(0x80 | length);
                Buffer.BlockCopy(mask, 0, h, 2, 4);
            }
            else if (length <= 65535)
            {
                h = new byte[8];
                h[1] = 0xFE;
                h[2] = (byte)(length >> 8); h[3] = (byte)length;
                Buffer.BlockCopy(mask, 0, h, 4, 4);
            }
            else
            {
                h = new byte[14];
                h[1] = 0xFF;
                for (int i = 0; i < 8; i++) h[2+i] = (byte)((long)length >> (56-8*i));
                Buffer.BlockCopy(mask, 0, h, 10, 4);
            }
            h[0] = 0x81; // FIN + text frame
            return h;
        }

        // WebSocket frame reader (server→client, unmasked)
        private async Task<string> ReadFrameAsync(CancellationToken ct)
        {
            // Read 2-byte header
            await ReadExactAsync(_rbuf, 0, 2, ct);
            bool fin    = (_rbuf[0] & 0x80) != 0;
            int  opcode = _rbuf[0] & 0x0F;
            long payLen = _rbuf[1] & 0x7F;

            if (payLen == 126)
            {
                await ReadExactAsync(_rbuf, 2, 2, ct);
                payLen = (_rbuf[2] << 8) | _rbuf[3];
            }
            else if (payLen == 127)
            {
                await ReadExactAsync(_rbuf, 2, 8, ct);
                payLen = 0;
                for (int i = 0; i < 8; i++) payLen = (payLen << 8) | _rbuf[2+i];
            }

            int len = (int)Math.Min(payLen, _rbuf.Length);
            await ReadExactAsync(_rbuf, 0, len, ct);

            if (opcode == 8) return ""; // close frame
            if (opcode == 9) { await WriteFrameAsync([], ct); return ""; } // ping→pong

            return Encoding.UTF8.GetString(_rbuf, 0, len);
        }

        private async Task ReadExactAsync(byte[] buf, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await _ns!.ReadAsync(buf.AsMemory(offset + read, count - read), ct);
                if (n == 0) throw new Exception("Connection closed");
                read += n;
            }
        }

        public void Dispose() { try { _tcp?.Close(); } catch { } }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<string?> WaitForPopupAsync(int port, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(800, ct);
            try
            {
                var json = await _http.GetStringAsync($"http://localhost:{port}/json", ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (!t.TryGetProperty("url", out var urlEl)) continue;
                    var url = urlEl.GetString() ?? "";
                    if ((url.Contains("accounts.google.com") || url.Contains("google.com/o/oauth2")) &&
                        t.TryGetProperty("webSocketDebuggerUrl", out var wu))
                        return wu.GetString();
                }
            }
            catch { }
        }
        return null;
    }

    private static string ExtractTikTokSession(string cookieJson)
    {
        if (string.IsNullOrEmpty(cookieJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(cookieJson);
            if (!doc.RootElement.TryGetProperty("result", out var r)) return "";
            if (!r.TryGetProperty("cookies", out var cookies)) return "";
            var sb = new StringBuilder();
            foreach (var c in cookies.EnumerateArray())
            {
                if (!c.TryGetProperty("name", out var n) || !c.TryGetProperty("value", out var v)) continue;
                var name = n.GetString() ?? "";
                if (name is "sessionid" or "sid_guard" or "uid_tt" or "uid_tt_ss" or "sid_tt" or "msToken")
                    sb.Append($"{name}={v.GetString()}; ");
            }
            return sb.ToString().TrimEnd();
        }
        catch { return ""; }
    }

    private static string? FindChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            var path = key?.GetValue(null)?.ToString();
            if (path != null && File.Exists(path)) return path;
        }
        catch { }
        return null;
    }

    private static string? FindGoogleProfile()
    {
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data");
        if (!Directory.Exists(userDataDir)) return null;
        try
        {
            var prefs = Path.Combine(userDataDir, "Default", "Preferences");
            if (File.Exists(prefs) && File.ReadAllText(prefs) is { } text &&
                text.Contains("account_info") && text.Contains("google.com"))
                return userDataDir;
        }
        catch { }
        return userDataDir;
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
