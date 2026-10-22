using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NomadInbox.Tests;

/// <summary>A tiny HTTP/1.1 server on 127.0.0.1 for exercising the real <c>PageFetcher</c>.</summary>
public sealed class TestWebServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public Dictionary<string, (int Status, string ContentType, byte[] Body)> Pages { get; } = [];

    public TestWebServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

    public void Add(string path, string contentType, string body, int status = 200) =>
        Pages[path] = (status, contentType, Encoding.UTF8.GetBytes(body));

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { } // headers

            var path = requestLine.Split(' ') is { Length: > 1 } parts ? parts[1] : "/";
            var (status, type, body) = Pages.TryGetValue(path, out var page)
                ? page
                : (404, "text/plain", "not found"u8.ToArray());

            var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\n" +
                         $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(body);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}
