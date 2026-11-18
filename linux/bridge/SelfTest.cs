namespace PokeIdleMulti;

internal static class SelfTest
{
    public static object Run()
    {
        var checks = new List<string>();
        void Check(string name, bool ok)
        {
            if (!ok) throw new InvalidOperationException("Self-test failed: " + name);
            checks.Add(name);
        }
        var config = new RestockConfig { On = true, GoldFloor = -1, Balls = new RestockRule { Keep = 500, Below = 100 } }.Normalized();
        Check("legacy restock Keep migrates to Buy", config.Balls.Buy == 400 && config.Balls.Keep is null && config.GoldFloor == 0);
        var guard = new BonusGuard();
        var slot = guard.SlotOf(0, LiveSite.Twitch);
        BonusGuard.Remember(slot, "online", "{\"points\":2000,\"cost\":300,\"paused\":false}");
        BonusGuard.Remember(slot, "offline", "{\"points\":400,\"cost\":300,\"paused\":false}");
        var picked = BonusGuard.Pick(slot, ["online", "offline"], new BonusSite { Channels = [new() { Login = "online", Live = true }] });
        Check("bonus guard spends offline points first", picked?.Channel == "offline");
        Check("bonus guard excludes open channels", BonusGuard.Candidates(["a", "b"], new BonusSite { Channels = [new() { Login = "c" }] }, ["b"]).SequenceEqual(["a", "c"]));
        var redeemer = new PointsRedeemer(LiveSite.Kick);
        redeemer.Apply("channel", "{\"points\":1000,\"cost\":300,\"title\":\"PokeIdle XP\",\"redeemed\":true}");
        redeemer.Apply("channel", "{\"points\":1000,\"cost\":300}");
        Check("refunded reward halts automatic spending", redeemer.Stopped is not null);
        var refused = new PointsRedeemer(LiveSite.Kick);
        refused.Apply("channel", "{\"points\":1000,\"cost\":300,\"status\":429}");
        Check("refused redemption observes backoff", refused.KickScript("https://kick.com", "channel", "not-a-token", KickReward.Xp).Contains("\"spend\":false"));
        Check("upstream game scripts embedded", Restock.Script.Contains("__deckRecompra") && Pvp.Script.Contains("__deckPvp") && Bonus.Script.Contains("__deckBonus"));
        return new { passed = checks.Count, checks };
    }
}
