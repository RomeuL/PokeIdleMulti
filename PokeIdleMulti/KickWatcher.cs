using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeIdleMulti;

/// <summary>
/// A Kick leve (Lives → Config…, por conta): assiste às lives da Kick sem player, mandando o que o player da Kick manda
/// por baixo (lido no código do player.kick.com em 05/11/2026). Pede o token de espectador com o login da conta, abre o
/// websocket de espectador (um por live, como um player em cada) e manda o "channel_handshake" a cada 15 s, o "ping" a
/// cada 30 s e o evento "tracking.user.watch.livestream" (o tempo assistido) no intervalo que a Kick pede (120 s de
/// fábrica). Sem vídeo: quase nada de CPU e memória. Roda na thread da interface (é criado nela).
/// </summary>
internal sealed class KickWatcher : IDisposable
{
    /// <summary>A API do site, a do websocket (o token) e o endereço do websocket.</summary>
    public sealed record Endpoints(string Api, string Websockets, string Socket);

    public static readonly Endpoints Kick = new("https://kick.com", "https://websockets.kick.com", "wss://websockets.kick.com/viewer/v1/connect");

    // O token público do site para o websocket de espectador (NEXT_PUBLIC_WEBSOCKET_CLIENT_TOKEN do player).
    internal const string ClientToken = "e1393935a959b4020a4491574f6490129f678acdaa92760471263db43487f823";

    // O mesmo navegador do deck (o player de verdade é o Edge embutido).
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Endpoints _endpoints;
    private readonly Dictionary<string, Live> _lives = new();
    private readonly CancellationTokenSource _stop = new();
    private TimeSpan? _asked;

    private sealed class Live
    {
        public required string Slug { get; init; }
        public required CancellationTokenSource Stop { get; init; }
        public JsonNode? ChannelId { get; set; }
        public JsonNode? LivestreamId { get; set; }
        public JsonNode? VodId { get; set; }
        public bool Offline { get; set; }
        public DateTime? WatchedAt { get; set; }
        public int Events { get; set; }
        public int Handshakes { get; set; }
        public int Received { get; set; }
        public string? Error { get; set; }
    }

    public KickWatcher(Endpoints endpoints, string token)
    {
        _endpoints = endpoints;
        Token = token;
    }

    /// <summary>O login da conta (o cookie session_token da Kick).</summary>
    public string Token { get; }

    /// <summary>De quanto em quanto tempo olha o que mandar, e os tempos do player (o autoteste encurta).</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan HandshakeEvery { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan PingEvery { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan InfoEvery { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>O intervalo do evento de tempo assistido; null: o que a Kick pede (120 s se não vier).</summary>
    public TimeSpan? WatchEvery { get; set; }

    /// <summary>As lives que a conta assiste agora (as abertas na janela das lives).</summary>
    public void SetChannels(IEnumerable<string> slugs)
    {
        var next = slugs.ToHashSet();
        foreach (var gone in _lives.Keys.Where(s => !next.Contains(s)).ToList())
        {
            _lives[gone].Stop.Cancel();
            _lives.Remove(gone);
        }
        foreach (var slug in next.Where(s => !_lives.ContainsKey(s)))
        {
            var live = new Live { Slug = slug, Stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token) };
            _lives[slug] = live;
            _ = RunAsync(live);
        }
    }

    /// <summary>"leve ✓ 14:05" (o último tempo assistido mandado), "leve…" (ainda não mandou), fora do ar ou o erro.</summary>
    public string Status(string slug) =>
        !_lives.TryGetValue(slug, out var live) ? ""
        : live.Error is string error ? $"leve: {error}"
        : live.Offline ? "leve: fora do ar"
        : live.WatchedAt is DateTime at ? $"leve ✓ {at:HH:mm}"
        : "leve…";

    /// <summary>Quantos eventos de tempo assistido e quantos handshakes mandou, e quantas mensagens a Kick mandou (autoteste e registro).</summary>
    public (int Events, int Handshakes, int Received) Counts(string slug) =>
        _lives.TryGetValue(slug, out var live) ? (live.Events, live.Handshakes, live.Received) : (0, 0, 0);

    private async Task RunAsync(Live live)
    {
        var stop = live.Stop.Token;
        int failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await InfoAsync(live, stop);
                string viewer = await ViewerTokenAsync(stop);
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("User-Agent", UserAgent);
                socket.Options.SetRequestHeader("Origin", "https://kick.com");
                await socket.ConnectAsync(new Uri($"{_endpoints.Socket}?token={Uri.EscapeDataString(viewer)}"), stop);
                live.Error = null;
                failures = 0;
                var every = WatchEvery ?? await AskedIntervalAsync(stop);
                var receiving = ReceiveAsync(socket, live);
                DateTime handshakeAt = DateTime.MinValue, watchAt = DateTime.MinValue;
                DateTime pingAt = DateTime.Now + PingEvery, infoAt = DateTime.Now + InfoEvery;
                try
                {
                    while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
                    {
                        var now = DateTime.Now;
                        if (now >= infoAt)
                        {
                            infoAt = now + InfoEvery;
                            try
                            {
                                await InfoAsync(live, stop);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                            {
                                // Fica com o que já sabia; tenta de novo na próxima.
                            }
                        }
                        if (now >= handshakeAt && live.ChannelId is JsonNode channelId)
                        {
                            handshakeAt = now + HandshakeEvery;
                            await SendAsync(socket, Message("channel_handshake", new JsonObject { ["channelId"] = channelId.ToString() }), stop);
                            live.Handshakes++;
                        }
                        if (now >= pingAt)
                        {
                            pingAt = now + PingEvery;
                            await SendAsync(socket, "{\"type\":\"ping\"}", stop);
                        }
                        // Como o player: o tempo assistido só vai com a live no ar e o id do VOD dela.
                        if (now >= watchAt && !live.Offline && live is { ChannelId: not null, LivestreamId: not null, VodId: not null })
                        {
                            watchAt = now + every;
                            await SendAsync(socket, Message("user_event", new JsonObject
                            {
                                ["name"] = "tracking.user.watch.livestream",
                                ["channel_id"] = live.ChannelId.DeepClone(),
                                ["livestream_id"] = live.LivestreamId.DeepClone(),
                                ["vod_id"] = live.VodId.DeepClone(),
                            }), stop);
                            live.Events++;
                            live.WatchedAt = DateTime.Now;
                        }
                        await Task.Delay(Tick, stop);
                    }
                    if (!stop.IsCancellationRequested)
                        live.Error = "a Kick fechou a conexão" + (socket.CloseStatus is WebSocketCloseStatus code ? $" ({(int)code})" : "");
                }
                finally
                {
                    // Saindo (a live fechou ou a Kick leve desligou): avisa, como o player, e fecha.
                    if (socket.State == WebSocketState.Open)
                    {
                        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try
                        {
                            if (live.ChannelId is JsonNode channelId)
                                await SendAsync(socket, Message("channel_disconnect", new JsonObject { ["channelId"] = channelId.ToString() }), quick.Token);
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", quick.Token);
                        }
                        catch
                        {
                            // Já caiu.
                        }
                    }
                    await receiving;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                live.Error = ex is TaskCanceledException or OperationCanceledException ? "a Kick não respondeu" : ex.Message;
            }
            failures++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(failures, 5))), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string Message(string type, JsonObject message) =>
        new JsonObject { ["type"] = type, ["data"] = new JsonObject { ["message"] = message } }.ToJsonString();

    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken stop) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stop);

    // Lê (e conta) o que a Kick manda, até a conexão fechar.
    private static async Task ReceiveAsync(ClientWebSocket socket, Live live)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var part = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (part.MessageType == WebSocketMessageType.Close)
                    return;
                if (part.EndOfMessage)
                    live.Received++;
            }
        }
        catch
        {
            // A conexão caiu: o laço de cima conecta de novo.
        }
    }

    private HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-app-platform", "web");
        request.Headers.TryAddWithoutValidation("Referer", "https://kick.com");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        return request;
    }

    // O canal (o id), a live no ar (o id) e o VOD dela: o que vai no tempo assistido.
    private async Task InfoAsync(Live live, CancellationToken stop)
    {
        using var request = Request($"{_endpoints.Api}/api/v2/channels/{Uri.EscapeDataString(live.Slug)}/info");
        using var response = await Http.SendAsync(request, stop);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"o canal não abriu (erro {(int)response.StatusCode})");
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(stop)) as JsonObject
                   ?? throw new InvalidOperationException("o canal veio sem dados");
        if (root["data"] is JsonObject wrapped && root["id"] is null)
            root = wrapped;
        live.ChannelId = root["id"]?.DeepClone() ?? throw new InvalidOperationException("o canal veio sem o id");
        var stream = root["livestream"] as JsonObject;
        live.Offline = stream is null || stream["is_live"]?.GetValueKind() == JsonValueKind.False;
        live.LivestreamId = stream?["id"]?.DeepClone();
        live.VodId = stream?["vod_id"] is JsonNode vod && vod.GetValueKind() != JsonValueKind.Null ? vod.DeepClone() : null;
    }

    // O token de espectador, com o login da conta (vale para uma conexão).
    private async Task<string> ViewerTokenAsync(CancellationToken stop)
    {
        using var request = Request(_endpoints.Websockets + "/viewer/v1/token");
        request.Headers.TryAddWithoutValidation("X-CLIENT-TOKEN", ClientToken);
        using var response = await Http.SendAsync(request, stop);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"a Kick recusou o token de espectador (erro {(int)response.StatusCode})");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop));
        return doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var token)
               && token.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException("a Kick não mandou o token de espectador");
    }

    // O intervalo que a Kick pede para o tempo assistido (o mesmo para todas as lives), 120 s se não vier.
    private async Task<TimeSpan> AskedIntervalAsync(CancellationToken stop)
    {
        if (_asked is TimeSpan known)
            return known;
        var asked = TimeSpan.FromSeconds(120);
        try
        {
            using var request = Request(_endpoints.Api + "/api/internal/settings/global");
            using var response = await Http.SendAsync(request, stop);
            if (response.IsSuccessStatusCode
                && JsonNode.Parse(await response.Content.ReadAsStringAsync(stop)) is JsonObject root
                && (root["events"] ?? root["data"]?["events"])?["tracking.user.watch.livestream"]?["interval"] is JsonNode interval
                && interval.GetValueKind() == JsonValueKind.Number)
                asked = TimeSpan.FromSeconds(Math.Clamp(interval.GetValue<double>(), 30, 600));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            // Fica com os 120 s do player.
        }
        _asked = asked;
        return asked;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lives.Clear();
    }
}
