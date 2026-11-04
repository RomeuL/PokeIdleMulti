using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Os avisos da Twitch de uma conta, na hora, pelo websocket de avisos da Twitch (PubSub), com o login da conta (o
/// cookie auth-token): o baú que apareceu, cada ponto que entrou (assistindo, baú, sequência de lives, raid) e, nos
/// canais abertos, a raid e o "momento". Assim o deck pega o baú sem esperar a conferência, entra na raid e sabe se as
/// lives estão rendendo. Roda na thread da interface (é criado nela): os avisos chegam lá. A conexão que cai volta
/// sozinha (5 s, 10 s… até 2 min); o login recusado (ERR_BADAUTH) para de vez.
/// </summary>
internal sealed class TwitchPubSub : IDisposable
{
    /// <summary>O endereço do websocket da Twitch.</summary>
    public const string TwitchUrl = "wss://pubsub-edge.twitch.tv/v1";

    // A Twitch fecha a conexão sem um PING a cada 5 min; sem o PONG em 15 s, a conexão caiu.
    private static readonly TimeSpan PingEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PongWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Um aviso: o tópico sem o id (community-points-user-v1, raid, community-moments-channel-v1), o tipo
    /// (claim-available, points-earned, raid_update_v2, active…), o id do canal e a mensagem inteira.
    /// </summary>
    public sealed record Notice(string Topic, string Type, string? ChannelId, JsonElement Message);

    private readonly string _url;
    private readonly Action<Notice> _notice;
    private readonly CancellationTokenSource _stop = new();
    private HashSet<string> _channels = new();
    private ClientWebSocket? _socket;
    private DateTime _pongAt;

    public TwitchPubSub(string url, string token, string userId, Action<Notice> notice)
    {
        _url = url;
        Token = token;
        UserId = userId;
        _notice = notice;
        _ = RunAsync();
    }

    public string Token { get; }
    public string UserId { get; }

    /// <summary>Conectado agora, e desde quando.</summary>
    public bool Connected { get; private set; }
    public DateTime? ConnectedAt { get; private set; }

    /// <summary>O que deu errado por último (null: nada); com o login recusado, parou.</summary>
    public string? Error { get; private set; }

    private string UserTopic => "community-points-user-v1." + UserId;

    private static IEnumerable<string> ChannelTopics(string channelId) =>
        new[] { "raid." + channelId, "community-moments-channel-v1." + channelId };

    /// <summary>Os canais abertos (os ids): ouve a raid e o momento de cada um, e para de ouvir os que fecharam.</summary>
    public void SetChannels(IEnumerable<string> channelIds)
    {
        var next = channelIds.ToHashSet();
        var added = next.Except(_channels).SelectMany(ChannelTopics).ToList();
        var removed = _channels.Except(next).SelectMany(ChannelTopics).ToList();
        _channels = next;
        if (_socket is { State: WebSocketState.Open } socket && Connected)
        {
            if (added.Count > 0)
                _ = SendAsync(socket, Listen("LISTEN", added));
            if (removed.Count > 0)
                _ = SendAsync(socket, Listen("UNLISTEN", removed));
        }
    }

    private string Listen(string type, IEnumerable<string> topics) =>
        JsonSerializer.Serialize(new { type, nonce = Guid.NewGuid().ToString("N"), data = new { topics, auth_token = Token } });

    private async Task RunAsync()
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!_stop.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            _socket = socket;
            bool done = false;
            try
            {
                await socket.ConnectAsync(new Uri(_url), _stop.Token);
                Connected = true;
                ConnectedAt = DateTime.Now;
                Error = null;
                wait = TimeSpan.FromSeconds(5);
                _pongAt = DateTime.Now;
                await SendAsync(socket, Listen("LISTEN", new[] { UserTopic }.Concat(_channels.SelectMany(ChannelTopics))));
                done = await ReceiveAsync(socket);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                done = true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                Connected = false;
                _socket = null;
            }
            if (done || _stop.IsCancellationRequested)
                break;
            try
            {
                await Task.Delay(wait, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxWait.Ticks));
        }
    }

    /// <summary>Lê os avisos até a conexão cair (false: conectar de novo) ou o login ser recusado (true: parar).</summary>
    private async Task<bool> ReceiveAsync(ClientWebSocket socket)
    {
        using var alive = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _ = PingAsync(socket, alive.Token);
        try
        {
            var buffer = new byte[16 * 1024];
            var text = new StringBuilder();
            while (socket.State == WebSocketState.Open)
            {
                var part = await socket.ReceiveAsync(buffer, alive.Token);
                if (part.MessageType == WebSocketMessageType.Close)
                    return false;
                text.Append(Encoding.UTF8.GetString(buffer, 0, part.Count));
                if (!part.EndOfMessage)
                    continue;
                var message = text.ToString();
                text.Clear();
                if (Handle(message) is bool stop)
                    return stop;
            }
            return false;
        }
        finally
        {
            alive.Cancel();
        }
    }

    /// <summary>Um texto do websocket. Devolve true para parar, false para conectar de novo, null para seguir.</summary>
    private bool? Handle(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "PONG":
                    _pongAt = DateTime.Now;
                    return null;
                case "RECONNECT":
                    return false;
                case "RESPONSE":
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                    if (string.IsNullOrEmpty(error))
                        return null;
                    Error = error == "ERR_BADAUTH" ? "a Twitch recusou o login da conta nos avisos" : error;
                    return error == "ERR_BADAUTH" ? true : null;
                case "MESSAGE":
                    var data = root.GetProperty("data");
                    var topic = data.GetProperty("topic").GetString() ?? "";
                    using (var inner = JsonDocument.Parse(data.GetProperty("message").GetString() ?? "{}"))
                    {
                        var message = inner.RootElement.Clone();
                        int dot = topic.IndexOf('.');
                        string name = dot < 0 ? topic : topic[..dot], suffix = dot < 0 ? "" : topic[(dot + 1)..];
                        string type = message.TryGetProperty("type", out var mt) ? mt.GetString() ?? "" : "";
                        _notice(new Notice(name, type, ChannelOf(name, suffix, message), message));
                    }
                    return null;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Aviso fora do formato: ignora esse e segue.
        }
        return null;
    }

    /// <summary>O canal do aviso: nos tópicos de canal é o do tópico; nos da conta vem na mensagem.</summary>
    private static string? ChannelOf(string topic, string suffix, JsonElement message)
    {
        if (topic != "community-points-user-v1")
            return suffix;
        if (!message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        static string? Id(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var p) ? p.TryGetProperty("channel_id", out var id) ? id.GetString() : null : null;
        return Id(data, "claim")
               ?? (data.TryGetProperty("channel_id", out var direct) ? direct.GetString() : null)
               ?? Id(data, "balance")
               ?? Id(data, "point_gain");
    }

    /// <summary>PING a cada minuto; sem o PONG a tempo, derruba a conexão (e o laço conecta de novo).</summary>
    private async Task PingAsync(ClientWebSocket socket, CancellationToken alive)
    {
        try
        {
            while (!alive.IsCancellationRequested)
            {
                await Task.Delay(PingEvery, alive);
                var sent = DateTime.Now;
                await SendAsync(socket, "{\"type\":\"PING\"}");
                await Task.Delay(PongWait, alive);
                if (_pongAt < sent)
                {
                    socket.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Um envio por vez (o websocket não aceita dois ao mesmo tempo: o PING e um LISTEN, por exemplo).
    private readonly SemaphoreSlim _sending = new(1, 1);

    private async Task SendAsync(ClientWebSocket socket, string text)
    {
        await _sending.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // A conexão caiu no meio: o laço conecta de novo e manda tudo outra vez.
        }
        finally
        {
            _sending.Release();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _socket?.Abort();
        }
        catch
        {
        }
    }
}
