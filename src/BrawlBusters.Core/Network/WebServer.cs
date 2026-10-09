using System.Net;
using System.Net.Sockets;
using System.Text;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Network;

/// <summary>
/// The two pages the client shows in its built-in browser: the notice panel of the main screen (GetADURL) and the
/// home page of the store (GetStoreHomeURL). The client takes the addresses from its executable
/// (tools/set_client_web.py points them here). Pages and pictures are plain files in the <c>web</c> folder, read on
/// every request, so they can be edited while the server runs.
/// </summary>
public sealed class WebServer
{
    private const int MaxRequestBytes = 8 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".ico"] = "image/x-icon",
        [".txt"] = "text/plain; charset=utf-8",
    };

    private readonly IPEndPoint _endPoint;
    private readonly string _folder;

    public WebServer(IPEndPoint endPoint, string folder)
    {
        _endPoint = endPoint;
        _folder = Path.GetFullPath(folder);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        WriteDefaultPages();

        var listener = new TcpListener(_endPoint);
        listener.Start();
        Log.Info("Web", $"Notice and shop pages on http://{_endPoint} (folder {_folder})");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = ServeAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                NetworkStream stream = client.GetStream();

                string? requestLine = await ReadRequestLineAsync(stream, timeout.Token);
                string[] parts = requestLine?.Split(' ') ?? [];
                if (parts.Length < 2 || parts[0] is not ("GET" or "HEAD"))
                {
                    await RespondAsync(stream, 400, "text/plain; charset=utf-8", "Bad request"u8.ToArray(), true, timeout.Token);
                    return;
                }

                string path = Uri.UnescapeDataString(parts[1].Split('?', '#')[0]);
                string? file = Resolve(path);
                bool withBody = parts[0] == "GET";
                if (file is null)
                {
                    Log.Debug(LogChannel.Network, "Web", $"{client.Client.RemoteEndPoint} {parts[0]} {path} -> 404");
                    await RespondAsync(stream, 404, "text/plain; charset=utf-8", "Not found"u8.ToArray(), withBody, timeout.Token);
                    return;
                }

                byte[] body = await File.ReadAllBytesAsync(file, timeout.Token);
                string type = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
                Log.Debug(LogChannel.Network, "Web", $"{client.Client.RemoteEndPoint} {parts[0]} {path} -> {Path.GetFileName(file)} ({body.Length} bytes)");
                await RespondAsync(stream, 200, type, body, withBody, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                Log.Error("Web", $"Request failed: {exception.Message}");
            }
        }
    }

    /// <summary>The file a request path stands for: <c>/notice</c> and <c>/shop</c> are the two pages, anything else a file of the folder.</summary>
    private string? Resolve(string path)
    {
        string name = path.Trim('/');
        if (name.Length == 0) name = "notice";
        if (!Path.HasExtension(name)) name += ".html";

        string full = Path.GetFullPath(Path.Combine(_folder, name));
        bool inside = full.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return inside && File.Exists(full) ? full : null;
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read <= 0) break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }

        int end = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
        return end <= 0 ? null : Encoding.ASCII.GetString(buffer, 0, end);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string contentType, byte[] body, bool withBody, CancellationToken cancellationToken)
    {
        string reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Error" };
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        if (withBody) await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private void WriteDefaultPages()
    {
        Directory.CreateDirectory(_folder);
        WriteIfMissing("notice.html", Page("Notice",
            "<h1>Welcome to the server</h1>\n<p>This panel is <code>web/notice.html</code> in the emulator folder. "
            + "Edit the file and reopen the main screen - no restart needed.</p>\n<ul>\n<li>Server news goes here.</li>\n</ul>"));
        WriteIfMissing("shop.html", Page("Store",
            "<h1>Store</h1>\n<p>This is <code>web/shop.html</code> in the emulator folder: the home page of the in-game store.</p>"));
    }

    private void WriteIfMissing(string name, string content)
    {
        string path = Path.Combine(_folder, name);
        if (!File.Exists(path)) File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <title>{{title}}</title>
        <style>
          html, body { margin: 0; padding: 0; background: #14161c; color: #e8e8ea; font-family: Tahoma, Verdana, sans-serif; font-size: 13px; }
          .box { padding: 12px 16px; }
          h1 { font-size: 18px; margin: 0 0 8px 0; color: #ffb320; }
          p, li { line-height: 1.5; }
          code { color: #9ad0ff; }
          a { color: #9ad0ff; }
        </style>
        </head>
        <body>
        <div class="box">
        {{body}}
        </div>
        </body>
        </html>
        """;
}
