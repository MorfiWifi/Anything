using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

// ============================================================================
// HttpFileServer — tiny read-only file browser for the client, built only on
// HttpListener (in-box, no NuGet). Directory listing + file download.
// ============================================================================
internal sealed class HttpFileServer : IDisposable
{
    private readonly string _root;
    private readonly int _port;
    private readonly string _hostIp;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public bool Running { get; private set; }
    public string Url => $"http://{_hostIp}:{_port}/";

    public HttpFileServer(string root, int port, string? hostIp = null)
    {
        _root = System.IO.Path.GetFullPath(root);
        _port = port;
        _hostIp = hostIp ?? Defaults.HostIp;
    }

    public bool Start(out string message)
    {
        message = "";
        try
        {
            Directory.CreateDirectory(_root);
            Stop(); // idempotent
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{_port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            Running = true;
            message = $"listening on port {_port}";
            return true;
        }
        catch (HttpListenerException ex)
        {
            message = $"cannot listen on port {_port}: {ex.Message} " +
                      $"(error {ex.ErrorCode}; another app may use the port, or run: " +
                      $"netsh http add urlacl url=http://*:{_port}/ user=Everyone)";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_listener?.IsListening == true) _listener.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; } // listener stopped
            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            // Map URL -> file, jailed inside _root (blocks "/../" escapes).
            string rel = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath.TrimStart('/') ?? "");
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Send(ctx, 403, "text/plain; charset=utf-8", "Forbidden"u8.ToArray());
                return;
            }

            if (Directory.Exists(full))
            {
                // Redirect /sub -> /sub/ so relative links work.
                if (!ctx.Request.Url!.AbsolutePath.EndsWith('/'))
                {
                    ctx.Response.Redirect(ctx.Request.Url.AbsolutePath + "/");
                    ctx.Response.Close();
                    return;
                }
                Send(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ListingPage(rel, full)));
            }
            else if (File.Exists(full))
            {
                ctx.Response.ContentType = MimeOf(full);
                ctx.Response.ContentLength64 = new FileInfo(full).Length;
                ctx.Response.StatusCode = 200;
                using var fs = File.OpenRead(full);
                fs.CopyTo(ctx.Response.OutputStream);
                ctx.Response.Close();
            }
            else
            {
                Send(ctx, 404, "text/plain; charset=utf-8", "Not found"u8.ToArray());
            }
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    private string ListingPage(string rel, string dir)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><title>WifiShare</title></head><body>");
        sb.Append("<h2>WifiShare file drop</h2><ul>");
        if (!string.IsNullOrEmpty(rel)) sb.Append("<li><a href='../'>.. (up)</a></li>");
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            string n = System.IO.Path.GetFileName(d);
            sb.Append($"<li>[dir] <a href='{WebUtility.UrlEncode(n)}/'>{WebUtility.HtmlEncode(n)}/</a></li>");
        }
        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
        {
            var fi = new FileInfo(f);
            sb.Append($"<li><a href='{WebUtility.UrlEncode(fi.Name)}'>{WebUtility.HtmlEncode(fi.Name)}</a> ({fi.Length} bytes)</li>");
        }
        sb.Append("</ul></body></html>");
        return sb.ToString();
    }

    private static void Send(HttpListenerContext ctx, int code, string type, byte[] body)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
    }

    private static string MimeOf(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            ".json" => "application/json",
            ".xml" => "text/xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
