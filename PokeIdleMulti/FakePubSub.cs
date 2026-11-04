using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// O websocket de avisos da Twitch do autoteste, em localhost: anota cada tópico que o deck pede (com o login), responde
/// o PING e manda os avisos que o autoteste quiser (o baú, os pontos, a raid, o momento).
/// </summary>
internal sealed class FakePubSub : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly List<(WebSocket Socket, HashSet<string> Topics)> _clients = new();
    private readonly List<string> _listens = new();

    public FakePubSub()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _http.Prefixes.Add($"http://localhost:{port}/");
        _http.Start();
        Url = $"ws://localhost:{port}/";
        _ = AcceptAsync();
    }

    public string Url { get; }

    /// <summary>Cada tópico pedido, com o login: "community-points-user-v1.id-x OAuth-token".</summary>
    public List<string> Listens
    {
        get
        {
            lock (_listens)
                return _listens.ToList();
        }
    }

    /// <summary>Algum deck conectado ouve <paramref name="topic"/> agora.</summary>
    public bool Listening(string topic)
    {
        lock (_clients)
            return _clients.Any(c => c.Topics.Contains(topic));
    }

    private async Task AcceptAsync()
    {
        while (_http.IsListening)
        {
            try
            {
                var context = await _http.GetContextAsync();
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                _ = ServeAsync(socket);
            }
            catch
            {
                // Fechou.
                return;
            }
        }
    }

    private async Task ServeAsync(WebSocket socket)
    {
        var topics = new HashSet<string>();
        lock (_clients)
            _clients.Add((socket, topics));
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var part = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (part.MessageType == WebSocketMessageType.Close)
                    break;
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, part.Count));
                var root = doc.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "PING":
                        await SendAsync(socket, "{\"type\":\"PONG\"}");
                        break;
                    case "LISTEN":
                    case "UNLISTEN":
                        bool listen = root.GetProperty("type").GetString() == "LISTEN";
                        var data = root.GetProperty("data");
                        string token = data.TryGetProperty("auth_token", out var t) ? t.GetString() ?? "" : "";
                        foreach (var topic in data.GetProperty("topics").EnumerateArray().Select(x => x.GetString() ?? ""))
                        {
                            lock (_clients)
                            {
                                if (listen)
                                    topics.Add(topic);
                                else
                                    topics.Remove(topic);
                            }
                            if (listen)
                                lock (_listens)
                                    _listens.Add($"{topic} {token}");
                        }
                        await SendAsync(socket, JsonSerializer.Serialize(new
                        {
                            type = "RESPONSE", nonce = root.GetProperty("nonce").GetString(), error = token == "recusado" ? "ERR_BADAUTH" : "",
                        }));
                        break;
                }
            }
        }
        catch
        {
            // O deck fechou a conexão.
        }
        finally
        {
            lock (_clients)
                _clients.RemoveAll(c => c.Socket == socket);
        }
    }

    /// <summary>Manda o aviso <paramref name="message"/> (o JSON de dentro, como a Twitch) a quem ouve <paramref name="topic"/>.</summary>
    public async Task SendAsync(string topic, object message)
    {
        var text = JsonSerializer.Serialize(new { type = "MESSAGE", data = new { topic, message = JsonSerializer.Serialize(message) } });
        List<WebSocket> to;
        lock (_clients)
            to = _clients.Where(c => c.Topics.Contains(topic)).Select(c => c.Socket).ToList();
        foreach (var socket in to)
            await SendAsync(socket, text);
    }

    private static readonly SemaphoreSlim Sending = new(1, 1);

    private static async Task SendAsync(WebSocket socket, string text)
    {
        await Sending.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
        }
        finally
        {
            Sending.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            _http.Stop();
            _http.Close();
        }
        catch
        {
        }
    }
}
