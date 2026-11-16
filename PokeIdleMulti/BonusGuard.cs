using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>O que o bonus.js da página devolve (<c>__deckBonus.summary()</c>): o bônus das lives da Twitch e da Kick no jogo.</summary>
internal sealed class BonusSummary
{
    public bool Connected { get; set; }

    /// <summary>null: o jogo não mandou o bloco.</summary>
    public BonusSite? Twitch { get; set; }
    public BonusSite? Kick { get; set; }
}

/// <summary>O script da página que lê o bônus das lives (bonus.js) e a leitura dele.</summary>
internal static class Bonus
{
    public const string SummaryScript = "window.__deckBonus ? JSON.stringify(window.__deckBonus.summary()) : null";

    public static string Script { get; } = ReadScript();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static BonusSummary? Parse(string? json)
    {
        try
        {
            var text = json is null or "null" ? null : JsonSerializer.Deserialize<string>(json);
            return text is null ? null : JsonSerializer.Deserialize<BonusSummary>(text, Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ReadScript()
    {
        using var stream = typeof(Bonus).Assembly.GetManifestResourceStream("bonus.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>O bônus das lives de um site no jogo (o bloco twitch ou kick do estado da conta, lido pelo bonus.js).</summary>
internal sealed class BonusSite
{
    /// <summary>A conta vinculou o site no jogo (sem isso os pontos voltam).</summary>
    public bool Linked { get; set; }

    /// <summary>Quantos segundos faltam para cada bônus vencer ("xp", "captura", "shiny"; 0: sem bônus).</summary>
    public Dictionary<string, int> Left { get; set; } = new();

    /// <summary>Os canais oficiais com os bônus do PokeIdle.</summary>
    public List<BonusChannel> Channels { get; set; } = new();
}

internal sealed class BonusChannel
{
    public string Login { get; set; } = "";
    public bool Live { get; set; }
}

/// <summary>
/// A guarda do bônus (Lives → Config…, por conta): o resgate automático só usa os pontos das lives abertas, e quando os
/// streamers delas saem do ar o bônus vence com pontos parados em outros canais. Com o bônus escolhido da conta num site
/// vencendo em menos de <see cref="Threshold"/> (ou vencido), a guarda lê o saldo dos canais da lista e dos oficiais do
/// jogo que não estão abertos na conta, escolhe um com os pontos (offline primeiro: os pontos dele estão parados; depois o
/// de mais pontos), e o deck resgata 1 ali. Depois confere no jogo se o tempo do bônus subiu. Aqui fica o estado e a
/// escolha; o resgate (numa página escondida do canal) é do deck.
/// </summary>
internal sealed class BonusGuard
{
    /// <summary>A partir de quanto falta para vencer a guarda age.</summary>
    public TimeSpan Threshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Quanto vale a leitura do saldo de um canal (o de canal offline nem muda).</summary>
    public TimeSpan ReadFor { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Depois do resgate, até quando o jogo tem para mostrar o tempo a mais.</summary>
    public TimeSpan ConfirmWithin { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>O nome do bônus no jogo ("xp", "captura", "shiny").</summary>
    public static string Kind(KickReward reward) => reward switch
    {
        KickReward.Capture => "captura",
        KickReward.Lure => "shiny",
        _ => "xp",
    };

    /// <summary>O nome do bônus para o registro ("XP", "Captura", "Shiny").</summary>
    public static string KindName(KickReward reward) => reward switch
    {
        KickReward.Capture => "Captura",
        KickReward.Lure => "Shiny",
        _ => "XP",
    };

    /// <summary>"35 min", "2 h 10 min".</summary>
    public static string Time(int secs) =>
        secs >= 3600 ? $"{secs / 3600} h {secs % 3600 / 60:00} min" : $"{Math.Max(1, secs / 60)} min";

    /// <summary>O estado da guarda numa conta e num site.</summary>
    public sealed class Slot
    {
        public bool Busy;
        public DateTime HoldUntil;

        /// <summary>O último resgate da guarda, ainda sem o jogo mostrar o tempo a mais, e quanto faltava antes dele.</summary>
        public DateTime? RedeemedAt;
        public int LeftBefore;

        /// <summary>Já disse no registro que nenhum canal tinha os pontos (volta quando o bônus sobe).</summary>
        public bool NoneNoted;

        /// <summary>A última leitura de cada canal: o saldo, o custo do bônus (o mais barato liberado) e se está pausado.</summary>
        public readonly Dictionary<string, (DateTime At, int? Points, int? Cost, bool Paused)> Reads = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<(int Account, LiveSite Site), Slot> _slots = new();

    public Slot SlotOf(int account, LiveSite site)
    {
        if (!_slots.TryGetValue((account, site), out var slot))
            _slots[(account, site)] = slot = new Slot();
        return slot;
    }

    /// <summary>Recomeça do zero (ao salvar Lives → Config…).</summary>
    public void Restart() => _slots.Clear();

    /// <summary>
    /// Os canais onde a guarda pode resgatar: os da lista do deck e os oficiais do jogo, sem os abertos agora na conta
    /// (neles quem resgata é o resgate automático).
    /// </summary>
    public static List<string> Candidates(IEnumerable<string> configured, BonusSite site, IEnumerable<string> open)
    {
        var skip = new HashSet<string>(open, StringComparer.OrdinalIgnoreCase);
        return configured.Concat(site.Channels.Select(c => c.Login)).Where(c => c.Length > 0 && !skip.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Precisa ler o canal de novo (sem leitura, ou mais velha que <see cref="ReadFor"/>).</summary>
    public bool Stale(Slot slot, string channel) =>
        !slot.Reads.TryGetValue(channel, out var read) || DateTime.Now - read.At > ReadFor;

    /// <summary>Guarda a leitura de um canal (o JSON das leituras sem a página: points, cost, paused).</summary>
    public static void Remember(Slot slot, string channel, string json)
    {
        int? points = null, cost = null;
        bool paused = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            points = root.TryGetProperty("points", out var p) && p.TryGetInt32(out int pv) ? pv : null;
            cost = root.TryGetProperty("cost", out var k) && k.TryGetInt32(out int kv) ? kv : null;
            paused = root.TryGetProperty("paused", out var z) && z.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            // Fica sem saldo: não entra na escolha.
        }
        slot.Reads[channel] = (DateTime.Now, points, cost, paused);
    }

    /// <summary>
    /// O canal para resgatar: com saldo para o bônus (o mais barato liberado) e o bônus não pausado; offline antes (os
    /// pontos dele estão parados), depois o de mais pontos. Null: nenhum tem.
    /// </summary>
    public static (string Channel, int Points, int Cost, bool Live)? Pick(Slot slot, IEnumerable<string> candidates, BonusSite site)
    {
        var live = new HashSet<string>(site.Channels.Where(c => c.Live).Select(c => c.Login), StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(c => slot.Reads.TryGetValue(c, out var r) && r is { Points: int pts, Cost: int cost, Paused: false } && pts >= cost)
            .Select(c => (Channel: c, Points: slot.Reads[c].Points!.Value, Cost: slot.Reads[c].Cost!.Value, Live: live.Contains(c)))
            .OrderBy(x => x.Live).ThenByDescending(x => x.Points)
            .Select(x => ((string, int, int, bool)?)x).FirstOrDefault();
    }
}
