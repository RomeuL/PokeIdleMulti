using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeIdleMulti;

internal enum LiveSite { Twitch, Kick }

// The upstream watchers assume a UI synchronization context. Give them the same
// single-threaded scheduling on Linux, without bringing WinForms into the build.
internal sealed class DeckContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    public override void Post(SendOrPostCallback callback, object? state)
    {
        try { _queue.Add((callback, state)); }
        catch (InvalidOperationException) when (_queue.IsAddingCompleted) { /* The process is shutting down. */ }
    }
    public void Run(Func<Task> work)
    {
        SetSynchronizationContext(this);
        var task = work();
        task.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        task.GetAwaiter().GetResult();
    }
}

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly string Root = Environment.GetEnvironmentVariable("DECK_DATA_DIR") ?? "/data";
    private static readonly string ConfigPath = Path.Combine(Root, "deck.json");
    private static DeckConfig _config = new();
    private static RedeemLog _log = null!;
    private static readonly Dictionary<(int, LiveSite), PointsRedeemer> Redeemers = new();
    private static readonly Dictionary<int, TwitchWatcher> Twitch = new();
    private static readonly Dictionary<int, KickWatcher> Kick = new();
    private static readonly Dictionary<int, TwitchPubSub> PubSub = new();
    private static readonly BonusGuard Guard = new();
    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    private static string S(JsonElement a, string key, string fallback = "") => a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    private static bool B(JsonElement a, string key, bool fallback = false) => a.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.True : fallback;
    private static int I(JsonElement a, string key, int fallback = 0) => a.TryGetProperty(key, out var v) && v.TryGetInt32(out int n) ? n : fallback;
    private static string[] Strings(JsonElement a, string key) => a.TryGetProperty(key, out var v) ? v.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
    private static LiveSite Site(JsonElement a) => Enum.Parse<LiveSite>(S(a, "site"), true);
    private static KickReward? Reward(JsonElement a) => string.IsNullOrEmpty(S(a, "reward")) ? null : Enum.Parse<KickReward>(S(a, "reward"), true);
    private static PointsRedeemer Redeemer(int account, LiveSite site)
    {
        if (!Redeemers.TryGetValue((account, site), out var r))
        {
            r = new PointsRedeemer(site) { Log = text => _log.Add(account, _config.AccountNames[account], site, text) };
            Redeemers[(account, site)] = r;
        }
        return r;
    }
    private static void Stop(int account)
    {
        if (Twitch.Remove(account, out var t)) t.Dispose();
        if (Kick.Remove(account, out var k)) k.Dispose();
        if (PubSub.Remove(account, out var p)) p.Dispose();
    }
    private static object Config() => JsonSerializer.SerializeToElement(_config, ConfigJson);
    private static object? _scripts;
    private static object Scripts() => _scripts ??= new
    {
        recompra = Restock.Script, pvp = Pvp.Script, bonus = Bonus.Script,
        echo = Echo.CaptureScript, frames = GameScripts.FrameLimitScript,
        sprites = GameScripts.SpriteLimitScript, open = GameScripts.OpenScript,
        login = GameScripts.LoginScript, ultra = GameScripts.SetUltraScript,
        ultraState = GameScripts.UltraStateScript,
        noDrm = LiveScripts.NoDrmScript, play = LiveScripts.PlayScript,
    };

    private static string BulkScript(JsonElement a)
    {
        var site = Site(a);
        var redeem = S(a, "operation") == "redeem";
        var body = site == LiveSite.Twitch
            ? redeem ? BulkScripts.TwitchRedeemBody : BulkScripts.TwitchReadBody
            : redeem ? BulkScripts.KickRedeemBody : BulkScripts.KickReadBody;
        var args = a.GetProperty("args").Deserialize<Dictionary<string, object?>>()!;
        args["clientId"] = PointsRedeemer.TwitchClientId;
        args["words"] = new[] { KickReward.Xp, KickReward.Capture, KickReward.Lure }.Select(PointsRedeemer.Word).ToArray();
        return "(async a => {" + (site == LiveSite.Kick ? BulkScripts.KickApi : PointsRedeemer.TwitchGql) + BulkScripts.Pick
            + " try {" + body + " } catch(e) { return {error: String(e.message || e)}; } })("
            + JsonSerializer.Serialize(args) + ").then(o => JSON.stringify(o))";
    }

    private static async Task<object?> Dispatch(string method, JsonElement a)
    {
        var account = I(a, "account");
        if (account is < 0 or > 3) throw new ArgumentOutOfRangeException("account");
        switch (method)
        {
            case "init": return new { config = Config(), scripts = Scripts(), balls = Restock.KnownBalls, potions = Restock.KnownPotions };
            case "config.save":
                var next = a.GetProperty("config").Deserialize<DeckConfig>(ConfigJson) ?? throw new ArgumentException("config");
                var temp = ConfigPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(next, ConfigJson));
                _config = DeckConfig.Load(temp);
                File.WriteAllText(temp, JsonSerializer.Serialize(_config, ConfigJson));
                File.Move(temp, ConfigPath, true);
                return Config();
            case "echo.replay":
                var click = Echo.Parse(S(a, "message"));
                return click is null ? null : Echo.ReplayScript(click);
            case "lives.check":
                var twitchChannels = Strings(a, "twitch");
                return new { twitch = await TwitchLive.LiveAsync(twitchChannels), kick = await KickLiveStates(Strings(a, "kick")) };
            case "watchers.stop": Stop(account); return true;
            case "watchers.stop-site":
                if (Site(a) == LiveSite.Twitch)
                {
                    if (Twitch.Remove(account, out var stoppedTwitch)) stoppedTwitch.Dispose();
                    if (PubSub.Remove(account, out var stoppedPubsub)) stoppedPubsub.Dispose();
                }
                else if (Kick.Remove(account, out var stoppedKick)) stoppedKick.Dispose();
                return true;
            case "watchers.sync":
                var site = Site(a);
                var token = S(a, "token");
                var channels = Strings(a, "channels");
                if (site == LiveSite.Twitch)
                {
                    var userId = S(a, "userId");
                    if (!Twitch.TryGetValue(account, out var watcher) || watcher.Token != token || watcher.UserId != userId)
                    {
                        watcher?.Dispose();
                        Twitch[account] = watcher = new TwitchWatcher(TwitchWatcher.Twitch, userId, token, S(a, "device"));
                    }
                    watcher.SetChannels(channels);
                    if (!PubSub.TryGetValue(account, out var pubsub) || pubsub.Token != token || pubsub.UserId != userId)
                    {
                        pubsub?.Dispose();
                        PubSub[account] = pubsub = new TwitchPubSub(TwitchPubSub.TwitchUrl, token, userId,
                            notice => Write(new { eventName = "twitch.notice", account, notice }));
                    }
                    pubsub.SetChannels(Strings(a, "channelIds"));
                }
                else
                {
                    if (!Kick.TryGetValue(account, out var watcher) || watcher.Token != token)
                    {
                        watcher?.Dispose();
                        Kick[account] = watcher = new KickWatcher(KickWatcher.Kick, token);
                    }
                    watcher.SetChannels(channels);
                }
                return true;
            case "watchers.status":
                return new {
                    twitch = Strings(a, "twitch").ToDictionary(c => c, c => Twitch.GetValueOrDefault(account)?.Status(c) ?? "sem login"),
                    kick = Strings(a, "kick").ToDictionary(c => c, c => Kick.GetValueOrDefault(account)?.Status(c) ?? "sem login"),
                    pubsub = PubSub.TryGetValue(account, out var ps) ? new { ps.Connected, ps.Error } : null,
                };
            case "redeem.restart":
                foreach (var r in Redeemers.Values) r.Restart();
                Guard.Restart(); return true;
            case "redeem.read":
                return Site(a) == LiveSite.Twitch
                    ? await PointsRedeemer.TwitchReadAsync("https://gql.twitch.tv", S(a, "channel"), S(a, "token"), S(a, "device"), Reward(a))
                    : await PointsRedeemer.KickReadAsync("https://kick.com", S(a, "channel"), S(a, "token"), Reward(a) ?? KickReward.Xp);
            case "redeem.needsPage": return Redeemer(account, Site(a)).NeedsPage(S(a, "channel"), S(a, "json"), B(a, "claim"));
            case "redeem.script":
                var redeemer = Redeemer(account, Site(a));
                return Site(a) == LiveSite.Twitch
                    ? redeemer.TwitchScript("https://gql.twitch.tv", S(a, "channel"), S(a, "token"), S(a, "device"), Reward(a), B(a, "claim"), B(a, "spend", true), a.TryGetProperty("claimId", out var ci) && ci.ValueKind == JsonValueKind.String ? ci.GetString() : null)
                    : redeemer.KickScript("https://kick.com", S(a, "channel"), S(a, "token"), Reward(a) ?? KickReward.Xp);
            case "redeem.apply":
                var rd = Redeemer(account, Site(a));
                var note = rd.Apply(S(a, "channel"), S(a, "json"));
                return new { note, rd.Stopped, rd.UserId, text = rd.Text(S(a, "channel")), id = rd.IdOf(S(a, "channel")) };
            case "redeem.earned":
                return Redeemer(account, LiveSite.Twitch).Earned(S(a, "channel"), S(a, "reason"), I(a, "points"), a.TryGetProperty("balance", out var ba) && ba.TryGetInt32(out int bn) ? bn : null);
            case "redeem.forget": Redeemer(account, Site(a)).Forget(S(a, "channel")); return true;
            case "twitch.raid": return PointsRedeemer.TwitchRaidScript("https://gql.twitch.tv", S(a, "token"), S(a, "device"), S(a, "id"));
            case "twitch.moment": return PointsRedeemer.TwitchMomentScript("https://gql.twitch.tv", S(a, "token"), S(a, "device"), S(a, "id"));
            case "bulk.script": return BulkScript(a);
            case "guard.pick":
                var slot = Guard.SlotOf(account, Site(a));
                foreach (var read in a.GetProperty("reads").EnumerateObject()) BonusGuard.Remember(slot, read.Name, read.Value.GetRawText());
                var bonus = a.GetProperty("bonus").Deserialize<BonusSite>(Json)!;
                var pick = BonusGuard.Pick(slot, Strings(a, "candidates"), bonus);
                return pick is null ? null : new { channel = pick.Value.Channel, points = pick.Value.Points, cost = pick.Value.Cost };
            case "log.add": _log.Add(account, _config.AccountNames[account], Site(a), S(a, "text")); return true;
            case "log.read": return B(a, "all") ? _log.Lines : _log.LinesOf(account);
            case "log.clear": _log.Clear(B(a, "all") ? null : account); return true;
            case "selftest":
                // Also exercise a continuation while the main loop awaits stdin.
                await Task.Delay(50);
                return SelfTest.Run();
            default: throw new ArgumentException("Unknown method");
        }
    }

    private static async Task<Dictionary<string, bool?>> KickLiveStates(string[] channels)
    {
        var result = new Dictionary<string, bool?>();
        foreach (var channel in channels) result[channel] = await KickLive.IsLiveAsync(channel);
        return result;
    }

    private static async Task Handle(string line)
    {
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            id = doc.RootElement.GetProperty("id").GetInt32();
            var method = doc.RootElement.GetProperty("method").GetString()!;
            var result = await Dispatch(method, doc.RootElement.GetProperty("args"));
            Write(new { id, result });
        }
        catch (Exception ex) { Write(new { id, error = ex.GetType().Name + ": " + ex.Message }); }
    }

    public static void Main() => new DeckContext().Run(async () =>
    {
        Directory.CreateDirectory(Root);
        _config = DeckConfig.Load(ConfigPath);
        _log = new RedeemLog(Root, _config.AccountNames);
        var requests = new List<Task>();
        // Console.In's synchronized reader can perform a blocking read even
        // through ReadLineAsync. The raw pipe's async StreamReader keeps live
        // watchers and their timers running while no RPC requests arrive.
        using var input = new StreamReader(Console.OpenStandardInput());
        while (await input.ReadLineAsync() is string line)
        {
            // Network waits must not block settings, power controls or logs.
            // Continuations still run on DeckContext, as in the WinForms UI.
            requests.RemoveAll(task => task.IsCompleted);
            requests.Add(Handle(line));
        }
        await Task.WhenAll(requests);
        foreach (int i in Enumerable.Range(0, 4)) Stop(i);
    });
}
