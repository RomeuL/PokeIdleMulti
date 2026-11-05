using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// A Twitch de fora do navegador do autoteste, em localhost. O websocket de avisos: anota cada tópico que o deck pede
/// (com o login), responde o PING e manda os avisos que o autoteste quiser (o baú, os pontos, a raid, o momento). E a
/// parte web que a Twitch leve usa: a página do canal (com a config), o aviso de "assistindo", a API (a live e o token
/// do vídeo) e as listas e os pedaços do vídeo. Cada pedido vai para <see cref="Web"/>.
/// </summary>
internal sealed class FakePubSub : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly List<(WebSocket Socket, HashSet<string> Topics)> _clients = new();
    private readonly List<string> _listens = new();
    private readonly List<string> _web = new();
    private readonly Dictionary<string, int> _sequence = new();

    public FakePubSub()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _http.Prefixes.Add($"http://localhost:{port}/");
        _http.Start();
        Url = $"ws://localhost:{port}/";
        WebUrl = $"http://localhost:{port}";
        _ = AcceptAsync();
    }

    public string Url { get; }

    /// <summary>A parte web (sem a / no fim).</summary>
    public string WebUrl { get; }

    /// <summary>
    /// Responde a leitura do saldo de fora da página (canal, Authorization, X-Device-Id → o JSON da Twitch). O deck liga
    /// na Twitch de mentira dele, para o saldo ser o mesmo do player.
    /// </summary>
    public Func<string, string, string, string?>? Points { get; set; }

    /// <summary>
    /// A Kick leve: responde o saldo e os bônus de fora da página (canal, "points" ou "rewards", Authorization → o JSON da
    /// Kick). O deck liga na Kick de mentira dele, para o saldo ser o mesmo do player.
    /// </summary>
    public Func<string, string, string, string?>? KickApi { get; set; }

    /// <summary>Os ids que a Kick de mentira dá ao canal e à live dele (o VOD é "vod-canal").</summary>
    public static int KickChannelId(string slug) => 1000 + slug.Sum(c => c);
    public static int KickLivestreamId(string slug) => 5000 + slug.Sum(c => c);

    /// <summary>
    /// Cada pedido da parte web: "página canal", "spade {json}", "info canal", "token canal login", "usher canal sig=…",
    /// "lista canal qualidade", "HEAD /seg/…" (e "GET /seg/…", que a Twitch leve não deve fazer).
    /// </summary>
    public List<string> Web
    {
        get
        {
            lock (_web)
                return _web.ToList();
        }
    }

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
                    _ = ServeWebAsync(context);
                    continue;
                }
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                if (context.Request.Url!.AbsolutePath.StartsWith("/viewer/v1/connect"))
                    _ = ServeKickAsync(socket, context.Request.QueryString["token"] ?? "");
                else
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

    /// <summary>
    /// O websocket de espectador da Kick: anota "kick conectou token", "kick handshake id", "kick assistiu canal live vod",
    /// "kick ping" e "kick saiu id".
    /// </summary>
    private async Task ServeKickAsync(WebSocket socket, string token)
    {
        Note($"kick conectou {token}");
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
                string type = root.GetProperty("type").GetString() ?? "";
                var message = root.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var m) ? m : default;
                switch (type)
                {
                    case "ping":
                        Note("kick ping");
                        await SendAsync(socket, "{\"type\":\"pong\"}");
                        break;
                    case "channel_handshake":
                        Note($"kick handshake {message.GetProperty("channelId")}");
                        break;
                    case "channel_disconnect":
                        Note($"kick saiu {message.GetProperty("channelId")}");
                        break;
                    case "user_event":
                        Note($"kick assistiu {message.GetProperty("name").GetString()} {message.GetProperty("channel_id").GetRawText()} " +
                             $"{message.GetProperty("livestream_id").GetRawText()} {message.GetProperty("vod_id").GetRawText()}");
                        break;
                    default:
                        Note($"kick {type}");
                        break;
                }
            }
        }
        catch
        {
            // O deck fechou a conexão.
        }
    }

    private void Note(string line)
    {
        lock (_web)
            _web.Add(line);
    }

    /// <summary>A parte web da Twitch de mentira (a que a Twitch leve usa).</summary>
    private async Task ServeWebAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            string path = request.Url!.AbsolutePath;
            string body = request.HasEntityBody ? await new StreamReader(request.InputStream).ReadToEndAsync() : "";
            int status = 200;
            string type = "text/plain", text = "";
            if (path == "/viewer/v1/token")
            {
                // O token de espectador da Kick: só com o login da conta e o token do site.
                string auth = request.Headers["Authorization"] ?? "sem login";
                bool site = request.Headers["X-CLIENT-TOKEN"] == KickWatcher.ClientToken;
                Note($"kick token {auth} {(site ? "site ok" : "sem o token do site")}");
                type = "application/json";
                if (auth.StartsWith("Bearer ") && site)
                    text = JsonSerializer.Serialize(new { data = new { token = "vt-" + auth[7..] } });
                else
                    status = 401;
            }
            else if (path == "/api/internal/settings/global")
            {
                type = "application/json";
                text = "{\"events\":{\"tracking.user.watch.livestream\":{\"interval\":90}}}";
            }
            else if (path.StartsWith("/api/v2/channels/") && path.Split('/', StringSplitOptions.RemoveEmptyEntries) is [_, _, _, var slug, var what])
            {
                type = "application/json";
                string auth = request.Headers["Authorization"] ?? "sem login";
                if (what == "info")
                {
                    Note($"kick info {slug} {auth}");
                    text = JsonSerializer.Serialize(new
                    {
                        id = KickChannelId(slug), slug, livestream = new { id = KickLivestreamId(slug), is_live = true, vod_id = "vod-" + slug },
                    });
                }
                else
                {
                    Note($"kick {what} {slug} {auth}");
                    text = KickApi?.Invoke(slug, what, auth) ?? "";
                    if (text.Length == 0)
                        status = 404;
                }
            }
            else if (path == "/spade" && request.HttpMethod == "POST")
            {
                // data=<o evento em JSON, em base64>
                string data = Uri.UnescapeDataString(body.StartsWith("data=") ? body[5..] : body);
                Note("spade " + Encoding.UTF8.GetString(Convert.FromBase64String(data)));
                status = 204;
            }
            else if (path.StartsWith("/config/settings"))
            {
                type = "application/javascript";
                text = $"window.__twilightSettings = {{\"spade_url\":\"{WebUrl}/spade\",\"outro\":1}};";
            }
            else if (path == "/gql" && request.HttpMethod == "POST")
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                type = "application/json";
                if (root.TryGetProperty("variables", out var vars) && vars.TryGetProperty("login", out var pointsLogin)
                    && root.TryGetProperty("query", out var pointsQuery) && pointsQuery.GetString()?.Contains("communityPoints") == true)
                {
                    // A leitura do saldo de fora da página (Twitch leve): responde o deck, com o mesmo saldo do player.
                    text = Points?.Invoke(pointsLogin.GetString() ?? "", request.Headers["Authorization"] ?? "-", request.Headers["X-Device-Id"] ?? "-")
                           ?? "{\"data\":null}";
                }
                else if (root.TryGetProperty("operationName", out var op) && op.GetString() == "PlaybackAccessToken")
                {
                    string login = root.GetProperty("variables").GetProperty("login").GetString() ?? "";
                    Note($"token {login} {request.Headers["Authorization"] ?? "sem login"}");
                    text = JsonSerializer.Serialize(new
                    {
                        data = new { streamPlaybackAccessToken = new { value = $"{{\"channel\":\"{login}\"}}", signature = "sig1" } },
                    });
                }
                else
                {
                    string login = root.GetProperty("variables").GetProperty("l").GetString() ?? "";
                    Note($"info {login}");
                    text = JsonSerializer.Serialize(new
                    {
                        data = new { user = new { id = "id-" + login, stream = new { id = "b-" + login, game = new { id = "g1", name = "PokeIdle" } } } },
                    });
                }
            }
            else if (path.StartsWith("/usher/api/channel/hls/"))
            {
                string login = Path.GetFileNameWithoutExtension(path);
                Note($"usher {login} sig={request.QueryString["sig"]}");
                type = "application/vnd.apple.mpegurl";
                text = "#EXTM3U\n" +
                       $"#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720\n{WebUrl}/media/{login}/720p.m3u8\n" +
                       $"#EXT-X-STREAM-INF:BANDWIDTH=230000,RESOLUTION=284x160\n{WebUrl}/media/{login}/160p.m3u8\n" +
                       $"#EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS=\"mp4a.40.2\"\n{WebUrl}/media/{login}/audio.m3u8\n";
            }
            else if (path.StartsWith("/media/"))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string login = parts[1], quality = Path.GetFileNameWithoutExtension(parts[2]);
                Note($"lista {login} {quality}");
                int n;
                lock (_sequence)
                    n = _sequence[login] = _sequence.GetValueOrDefault(login) + 1;
                type = "application/vnd.apple.mpegurl";
                text = $"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{n}\n" +
                       string.Concat(Enumerable.Range(n, 3).Select(s => $"#EXTINF:2.000,live\n/seg/{login}/{s}.ts\n"));
            }
            else if (path.StartsWith("/seg/"))
            {
                Note($"{request.HttpMethod} {path}");
            }
            else if (request.HttpMethod == "GET" && path.Count(c => c == '/') == 1 && path.Length > 1)
            {
                Note($"página {path[1..]}");
                type = "text/html";
                text = $"<html><head><script src=\"{WebUrl}/config/settings.test123.js\"></script></head><body></body></html>";
            }
            else
            {
                status = 404;
            }
            response.StatusCode = status;
            response.ContentType = type;
            if (status != 204 && request.HttpMethod != "HEAD" && text.Length > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
            }
        }
        catch
        {
            response.StatusCode = 500;
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
            }
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
