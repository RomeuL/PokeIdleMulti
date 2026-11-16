using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>O que o pvp.js da página devolve (<c>__deckPvp.status()</c>).</summary>
internal sealed class PvpStatus
{
    public bool On { get; set; }
    public bool Connected { get; set; }

    /// <summary>O estado do socket do jogo visto pelo script (-1: nenhum; 0 abrindo, 1 aberto, 2/3 fechando/fechado).</summary>
    public int Socket { get; set; } = -1;

    /// <summary>Quantas mensagens do jogo o script viu, se viu o welcome e se tem o estado da conta.</summary>
    public int Seen { get; set; }
    public bool Welcomed { get; set; }
    public bool State { get; set; }

    public bool Vip { get; set; }

    /// <summary>A auto-fila do próprio jogo (só VIP) está ligada.</summary>
    public bool Auto { get; set; }

    /// <summary>A "Tela da batalha" do PvP do jogo está em Off: as partidas não abrem na tela.</summary>
    public bool Hidden { get; set; }

    /// <summary>A "Tela da batalha" do jogo agora (completa, mini, off ou null sem nada salvo) e a que o deck mandou manter.</summary>
    public string? Screen { get; set; }
    public string? WantScreen { get; set; }

    /// <summary>"economia" (Modo Economia ligado) ou "ultra" (a tela ULTRA à vista): o jogo esconde as partidas de qualquer jeito.</summary>
    public string? NoScene { get; set; }

    /// <summary>O Battery Saver está desligado porque o deck desligou (para mostrar as partidas); ele religa ao esconder.</summary>
    public bool EcoByDeck { get; set; }

    /// <summary>A velocidade que o deck mandou (0 = a do jogo), se o replay do jogo já aceita ela, o porquê se não, e a última aplicada.</summary>
    public double WantSpeed { get; set; }
    public bool SpeedReady { get; set; }
    public string? SpeedError { get; set; }
    public double LastSpeed { get; set; }

    /// <summary>Na fila agora, há quantos segundos, e quantos na fila.</summary>
    public bool Na { get; set; }
    public double NaSegundos { get; set; }
    public double Tamanho { get; set; }

    /// <summary>A espera que o jogo pediu antes de entrar de novo (entre partidas, ou depois de recusar).</summary>
    public double EsperaSegundos { get; set; }

    public double Wins { get; set; }
    public double Losses { get; set; }
    public double? Points { get; set; }
    public PvpMatch? LastMatch { get; set; }
    public List<string> Log { get; set; } = new();
}

internal sealed class PvpMatch
{
    public bool Won { get; set; }
    public string? Rival { get; set; }
    public double Delta { get; set; }
}

/// <summary>
/// A "Tela da batalha" do PvP do jogo (localStorage <c>pvpTelaBatalha</c>), que o ⚔ ligado mantém na escolhida, e as
/// velocidades das partidas mostradas (o replay do jogo tem 1×, 2×, 4× e 8× e aceita até 32×).
/// </summary>
internal static class PvpScreens
{
    public const string Full = "completa";
    public const string Mini = "mini";
    public const string Off = "off";

    public static readonly double[] Speeds = { 1, 2, 4, 8, 16, 32 };

    public static string Normalized(string? screen) => screen is Full or Mini ? screen : Off;

    /// <summary>0 (a do jogo) ou uma das <see cref="Speeds"/>.</summary>
    public static double NormalizedSpeed(double speed) => Speeds.Contains(speed) ? speed : 0;

    public static string Label(string screen) => Normalized(screen) switch
    {
        Full => "mostrada",
        Mini => "minimizada",
        _ => "escondida",
    };

    /// <summary>Por que o jogo esconde as partidas mesmo com a tela mostrada ou minimizada (null: não esconde).</summary>
    public static string? NoSceneNote(string? noScene) => noScene switch
    {
        "economia" => "Battery Saver (Modo Economia) ligado nesta conta: com ele o jogo esconde as partidas. Com a fila ligada e " +
                      "a luta mostrada ou minimizada, o deck desliga ele (e religa ao voltar para Escondida ou desligar a fila)",
        "ultra" => "Tela ULTRA ligada nesta conta: o jogo esconde as partidas assim mesmo (só o aviso do resultado)",
        _ => null,
    };

    public static string SpeedLabel(double speed) => NormalizedSpeed(speed) is > 0 and var s ? $"{s:0}×" : "a do jogo";
}

/// <summary>A fila de PvP de cada conta (⚔ no cabeçalho e no menu da conta): o script da página e a leitura dele.</summary>
internal static class Pvp
{
    // O resumo da página; um erro dentro dele volta como { error } (para a dica dizer o quê), não como "sem resposta".
    public const string StatusScript =
        "(() => { try { return window.__deckPvp ? JSON.stringify(window.__deckPvp.status()) : null; }" +
        " catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); } })()";

    /// <summary>Manda a tela e a velocidade das partidas e liga ou desliga a fila (nessa ordem: a 1ª volta ligada já usa a tela).</summary>
    public static string SetScript(bool on, string screen, double speed) =>
        "window.__deckPvp ? ((window.__deckPvp.config && window.__deckPvp.config(" +
        JsonSerializer.Serialize(new { tela = PvpScreens.Normalized(screen), vel = PvpScreens.NormalizedSpeed(speed) }) +
        $")), window.__deckPvp.set({(on ? "true" : "false")})) : false";

    /// <summary>O script da página (pvp.js).</summary>
    public static string Script { get; } = ReadScript();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                         | System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>O último resumo que não deu para ler (para a dica dizer o porquê).</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Lê o resumo campo a campo: um campo de tipo inesperado (o jogo mudou algo) fica com o valor de fábrica em vez de
    /// derrubar a leitura inteira.
    /// </summary>
    public static PvpStatus? Parse(string? json)
    {
        try
        {
            var text = json is null or "null" ? null : JsonSerializer.Deserialize<string>(json);
            if (text is null)
                return null;
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var error))
            {
                LastError = error.ToString();
                return null;
            }
            bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
                : null;
            var status = new PvpStatus
            {
                On = B(r, "on"), Connected = B(r, "connected"), Socket = r.TryGetProperty("socket", out var sock) && sock.ValueKind == JsonValueKind.Number ? sock.GetInt32() : -1,
                Seen = (int)D(r, "seen"), Welcomed = B(r, "welcomed"), State = B(r, "state"), Vip = B(r, "vip"), Auto = B(r, "auto"), Hidden = B(r, "hidden"),
                Na = B(r, "na"), NaSegundos = D(r, "naSegundos"), Tamanho = D(r, "tamanho"), EsperaSegundos = D(r, "esperaSegundos"),
                Wins = D(r, "wins"), Losses = D(r, "losses"),
                Screen = S(r, "tela"), NoScene = S(r, "semCena"), EcoByDeck = B(r, "ecoDoDeck"), WantScreen = S(r, "querTela"), WantSpeed = D(r, "querVel"), SpeedReady = B(r, "velPronta"),
                SpeedError = S(r, "velErro"), LastSpeed = D(r, "velUltima"),
                Points = r.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Number ? pts.GetDouble() : null,
            };
            if (r.TryGetProperty("lastMatch", out var m) && m.ValueKind == JsonValueKind.Object)
                status.LastMatch = new PvpMatch { Won = B(m, "won"), Rival = S(m, "rival"), Delta = D(m, "delta") };
            if (r.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Array)
                status.Log = log.EnumerateArray().Select(l => l.ToString()).ToList();
            LastError = null;
            return status;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>Por que a fila não está conectada (a primeira linha da dica do ⚔).</summary>
    public static string Waiting(PvpStatus? status) => status switch
    {
        null => LastError is string error ? $"não deu para ler a fila na página ({error})"
            : "o script da fila não respondeu na página (o deck tenta pôr de novo; se seguir, recarregue a conta no ⟳)",
        { On: false } => "ligando…",
        { Socket: -1 } => "o script não achou o socket do jogo (recarregue a conta no ⟳)",
        { Socket: 0 } => "o jogo está conectando",
        { Socket: > 1 } => "o socket do jogo caiu; esperando reconectar",
        _ => "esperando o jogo",
    };

    /// <summary>"3 min", "1 h 05": o tempo curto da dica.</summary>
    public static string Duration(double secs)
    {
        int seconds = (int)Math.Max(0, Math.Round(secs));
        return seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60:00}" : seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} s";
    }

    private static string ReadScript()
    {
        using var stream = typeof(Pvp).Assembly.GetManifestResourceStream("pvp.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
