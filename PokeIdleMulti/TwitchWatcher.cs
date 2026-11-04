using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PokeIdleMulti;

/// <summary>
/// A Twitch leve (Lives → Config…, por conta): assiste às lives da Twitch sem player, como os mineradores de pontos. A
/// cada minuto manda à Twitch o aviso "minute-watched" de cada live (o mesmo que o player manda), com o id da conta; e,
/// como um player, pede a lista dos pedaços do vídeo na menor qualidade e só o cabeçalho (HEAD) de cada pedaço novo, sem
/// baixar o vídeo. Quase nada de CPU e memória. Os avisos da Twitch (<see cref="TwitchPubSub"/>) mostram se rende (os
/// pontos de assistir). Roda na thread da interface (é criado nela).
/// </summary>
internal sealed class TwitchWatcher : IDisposable
{
    /// <summary>Onde ficam a página do canal (de onde sai o endereço do aviso), a API e as listas do vídeo.</summary>
    public sealed record Endpoints(string Site, string Gql, string Usher);

    public static readonly Endpoints Twitch = new("https://www.twitch.tv", "https://gql.twitch.tv/gql", "https://usher.ttvnw.net");

    // O mesmo navegador do deck (o player de verdade é o Edge embutido).
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    // A operação salva da Twitch que dá o token do vídeo (a mesma do player).
    private const string PlaybackHash = "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // O endereço do aviso de "assistindo" (spade), por site: sai da config da página da Twitch e vale umas horas.
    private static readonly Dictionary<string, (string Url, DateTime At)> Spade = new();
    private static readonly TimeSpan SpadeFor = TimeSpan.FromHours(6);

    private static readonly Regex SettingsJs = new(@"(https?://[^""'\s]+/config/settings[^""'\s]*?\.js)");
    private static readonly Regex SpadeUrl = new(@"""spade_url""\s*:\s*""(.*?)""");
    private static readonly Regex Bandwidth = new(@"(?<![-A-Z])BANDWIDTH=(\d+)");

    private readonly Endpoints _endpoints;
    private readonly string? _device;
    private readonly Dictionary<string, Live> _lives = new();
    private readonly CancellationTokenSource _stop = new();

    private sealed class Live
    {
        public required string Login { get; init; }
        public string? ChannelId { get; set; }
        public string? BroadcastId { get; set; }
        public string? Game { get; set; }
        public string? GameId { get; set; }
        public bool Offline { get; set; }
        public DateTime InfoAt { get; set; }
        public DateTime MinuteAt { get; set; } = DateTime.Now;
        public DateTime PlaylistAt { get; set; }
        public string? MediaUrl { get; set; }
        public DateTime MediaAt { get; set; }
        public HashSet<string> Seen { get; } = new();
        public Queue<string> SeenOrder { get; } = new();
        public DateTime? WatchedAt { get; set; }
        public int Minutes { get; set; }
        public int Segments { get; set; }
        public string? Error { get; set; }
    }

    public TwitchWatcher(Endpoints endpoints, string userId, string token, string? device)
    {
        _endpoints = endpoints;
        UserId = userId;
        Token = token;
        _device = device;
        _ = RunAsync();
    }

    public string UserId { get; }
    public string Token { get; }

    /// <summary>De quanto em quanto tempo olha cada live, manda o aviso de assistindo, pede a lista dos pedaços e relê a live.</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MinuteEvery { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan PlaylistEvery { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan InfoEvery { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>As lives que a conta assiste agora (as abertas na janela das lives, no máximo 2: o que a Twitch conta).</summary>
    public void SetChannels(IEnumerable<string> logins)
    {
        var next = logins.ToHashSet();
        foreach (var gone in _lives.Keys.Where(l => !next.Contains(l)).ToList())
            _lives.Remove(gone);
        foreach (var login in next.Where(l => !_lives.ContainsKey(l)))
            _lives[login] = new Live { Login = login };
    }

    /// <summary>"leve ✓ 14:05" (o último aviso de assistindo que a Twitch aceitou), "leve…" (ainda não mandou) ou o erro.</summary>
    public string Status(string login) =>
        !_lives.TryGetValue(login, out var live) ? ""
        : live.Error is string error ? $"leve: {error}"
        : live.WatchedAt is DateTime at ? $"leve ✓ {at:HH:mm}"
        : "leve…";

    /// <summary>Quantos avisos de assistindo a Twitch aceitou e quantos pedaços do vídeo pediu, por live (para o autoteste).</summary>
    public (int Minutes, int Segments) Counts(string login) =>
        _lives.TryGetValue(login, out var live) ? (live.Minutes, live.Segments) : (0, 0);

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (var live in _lives.Values.ToList())
            {
                if (_stop.IsCancellationRequested)
                    return;
                try
                {
                    await StepAsync(live);
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    live.Error = ex is TaskCanceledException ? "a Twitch não respondeu" : ex.Message;
                }
            }
            try
            {
                await Task.Delay(Tick, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task StepAsync(Live live)
    {
        var now = DateTime.Now;
        if (live.ChannelId is null || now - live.InfoAt >= InfoEvery)
            await InfoAsync(live);
        if (live.Offline || live.BroadcastId is null || !_lives.ContainsKey(live.Login))
            return;
        if (now - live.PlaylistAt >= PlaylistEvery)
        {
            live.PlaylistAt = now;
            await SegmentsAsync(live);
        }
        if (now - live.MinuteAt >= MinuteEvery)
        {
            live.MinuteAt = now;
            await MinuteAsync(live);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, bool login = false)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (url.StartsWith(_endpoints.Gql))
        {
            request.Headers.TryAddWithoutValidation("Client-Id", PointsRedeemer.TwitchClientId);
            if (login)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Token);
                if (_device is not null)
                    request.Headers.TryAddWithoutValidation("X-Device-Id", _device);
            }
        }
        return request;
    }

    private async Task<JsonElement?> GqlAsync(object body, bool login)
    {
        using var request = Request(HttpMethod.Post, _endpoints.Gql, login);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain");
        using var response = await Http.SendAsync(request, _stop.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_stop.Token));
        return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data.Clone() : null;
    }

    /// <summary>O id do canal, o da transmissão e o jogo (o que vai no aviso); sem transmissão, a live está offline.</summary>
    private async Task InfoAsync(Live live)
    {
        live.InfoAt = DateTime.Now;
        var data = await GqlAsync(new
        {
            query = "query($l:String!){user(login:$l){id stream{id game{id name}}}}",
            variables = new { l = live.Login },
        }, login: false);
        if (data is not JsonElement d || !d.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            live.Error = "a Twitch não achou o canal";
            return;
        }
        live.ChannelId = user.GetProperty("id").GetString();
        if (!user.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object)
        {
            live.Offline = true;
            live.Error = "offline";
            return;
        }
        string? broadcast = stream.GetProperty("id").GetString();
        if (broadcast != live.BroadcastId)
        {
            // Outra transmissão: o vídeo é outro.
            live.MediaUrl = null;
            live.Seen.Clear();
            live.SeenOrder.Clear();
        }
        live.BroadcastId = broadcast;
        live.Offline = false;
        if (stream.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Object)
        {
            live.Game = game.GetProperty("name").GetString();
            live.GameId = game.GetProperty("id").GetString();
        }
        if (live.Error == "offline")
            live.Error = null;
    }

    /// <summary>O aviso de "assistindo" de um minuto, como o player manda: o evento em JSON, em base64, no campo data.</summary>
    private async Task MinuteAsync(Live live)
    {
        var spade = await SpadeAsync(live.Login);
        if (spade is null)
        {
            live.Error = "sem o endereço do aviso de assistindo";
            return;
        }
        var properties = new Dictionary<string, object?>
        {
            ["channel_id"] = live.ChannelId,
            ["broadcast_id"] = live.BroadcastId,
            ["player"] = "site",
            ["user_id"] = UserId,
            ["live"] = true,
            ["channel"] = live.Login,
        };
        if (live.Game is not null && live.GameId is not null)
        {
            properties["game"] = live.Game;
            properties["game_id"] = live.GameId;
        }
        var json = JsonSerializer.Serialize(new[] { new { @event = "minute-watched", properties } });
        using var request = Request(HttpMethod.Post, spade);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) });
        using var response = await Http.SendAsync(request, _stop.Token);
        if (response.IsSuccessStatusCode)
        {
            live.WatchedAt = DateTime.Now;
            live.Minutes++;
            live.Error = null;
        }
        else
        {
            live.Error = $"aviso de assistindo: erro {(int)response.StatusCode}";
            // O endereço pode ter mudado: busca de novo na próxima vez.
            lock (Spade)
                Spade.Remove(_endpoints.Site);
        }
    }

    /// <summary>O endereço do aviso de "assistindo": está na config da página da Twitch (settings…js → spade_url).</summary>
    private async Task<string?> SpadeAsync(string login)
    {
        lock (Spade)
            if (Spade.TryGetValue(_endpoints.Site, out var known) && DateTime.Now - known.At < SpadeFor)
                return known.Url;
        using var page = Request(HttpMethod.Get, $"{_endpoints.Site}/{Uri.EscapeDataString(login)}");
        using var pageResponse = await Http.SendAsync(page, _stop.Token);
        var settings = SettingsJs.Match(await pageResponse.Content.ReadAsStringAsync(_stop.Token));
        if (!settings.Success)
            return null;
        using var config = Request(HttpMethod.Get, settings.Groups[1].Value);
        using var configResponse = await Http.SendAsync(config, _stop.Token);
        var spade = SpadeUrl.Match(await configResponse.Content.ReadAsStringAsync(_stop.Token));
        if (!spade.Success)
            return null;
        // O texto vem como dentro de um JSON (com \/ no lugar de /, às vezes).
        string url = JsonSerializer.Deserialize<string>($"\"{spade.Groups[1].Value}\"") ?? spade.Groups[1].Value;
        lock (Spade)
            Spade[_endpoints.Site] = (url, DateTime.Now);
        return url;
    }

    /// <summary>
    /// Como um player: a lista dos pedaços do vídeo na menor qualidade e só o cabeçalho (HEAD) de cada pedaço novo. A
    /// lista (com o token do vídeo) é pedida de novo a cada 10 min ou quando a Twitch recusa.
    /// </summary>
    private async Task SegmentsAsync(Live live)
    {
        if (live.MediaUrl is null || DateTime.Now - live.MediaAt > TimeSpan.FromMinutes(10))
        {
            live.MediaUrl = await MediaUrlAsync(live);
            live.MediaAt = DateTime.Now;
        }
        if (live.MediaUrl is not string media)
            return;
        using var request = Request(HttpMethod.Get, media);
        using var response = await Http.SendAsync(request, _stop.Token);
        if (!response.IsSuccessStatusCode)
        {
            live.MediaUrl = null;
            return;
        }
        var lines = (await response.Content.ReadAsStringAsync(_stop.Token)).Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines.Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var segment = new Uri(new Uri(media), line).ToString();
            if (!live.Seen.Add(segment))
                continue;
            live.SeenOrder.Enqueue(segment);
            while (live.SeenOrder.Count > 300)
                live.Seen.Remove(live.SeenOrder.Dequeue());
            using var head = Request(HttpMethod.Head, segment);
            using var headResponse = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
            live.Segments++;
        }
    }

    /// <summary>O token do vídeo (com o login da conta; sem ele, se a Twitch recusar) e a lista da menor qualidade com imagem.</summary>
    private async Task<string?> MediaUrlAsync(Live live)
    {
        object Body() => new
        {
            operationName = "PlaybackAccessToken",
            variables = new { login = live.Login, isLive = true, isVod = false, vodID = "", playerType = "site", platform = "web" },
            extensions = new { persistedQuery = new { version = 1, sha256Hash = PlaybackHash } },
        };
        JsonElement? token = null;
        foreach (bool login in new[] { true, false })
        {
            var data = await GqlAsync(Body(), login);
            if (data is JsonElement d && d.TryGetProperty("streamPlaybackAccessToken", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                token = t;
                break;
            }
        }
        if (token is not JsonElement access)
            return null;
        string master = $"{_endpoints.Usher}/api/channel/hls/{Uri.EscapeDataString(live.Login)}.m3u8" +
                        $"?sig={Uri.EscapeDataString(access.GetProperty("signature").GetString() ?? "")}" +
                        $"&token={Uri.EscapeDataString(access.GetProperty("value").GetString() ?? "")}" +
                        $"&allow_source=true&allow_audio_only=true&player=twitchweb&type=any&p={Random.Shared.Next(1_000_000, 9_999_999)}";
        using var request = Request(HttpMethod.Get, master);
        using var response = await Http.SendAsync(request, _stop.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        var lines = (await response.Content.ReadAsStringAsync(_stop.Token)).Replace("\r\n", "\n").Split('\n');
        var variants = new List<(long Bandwidth, bool Video, string Uri)>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:"))
                continue;
            int next = i + 1;
            while (next < lines.Length && (lines[next].Length == 0 || lines[next].StartsWith('#')))
                next++;
            if (next >= lines.Length)
                break;
            var match = Bandwidth.Match(lines[i]);
            variants.Add((match.Success ? long.Parse(match.Groups[1].Value) : long.MaxValue, lines[i].Contains("RESOLUTION="),
                new Uri(new Uri(master), lines[next]).ToString()));
            i = next;
        }
        var pick = variants.Where(v => v.Video).OrderBy(v => v.Bandwidth).Select(v => v.Uri).FirstOrDefault()
                   ?? variants.Select(v => v.Uri).LastOrDefault();
        return pick;
    }

    public void Dispose() => _stop.Cancel();
}
