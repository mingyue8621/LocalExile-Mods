using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

using static Game;

namespace GameServer.Modules.PlayerBots;

public static partial class BotPlay
{
    // ---- what a party bot is like of its own accord ----
    // A follower used to be one behaviour for every bot: form up, hit what stood near the leader, drink at a third life. Five
    // of them read as five copies of one script, and a player walking a map had a train behind him rather than a party
    // (live 2026-11-10: "the bots just follow, they have no ideas of their own"). Each bot now carries a temperament of its
    // own, grown from a hash of its name so it keeps it in every area and every session: how far from the player it will go
    // after an enemy, how readily it peels one off the player's back, whether it gives ground or stands, and how much it
    // says in party chat.

    internal sealed class ControlledMind
    {
        /// <summary>0 keeps to the player's pocket, 1 uses the whole sight (<see cref="ControlledSight"/>).</summary>
        public double Charge;
        /// <summary>0 fights to the end, 1 gives ground to a crowd that got inside its reach.</summary>
        public double Caution;
        /// <summary>0 hits whatever is nearest, 1 goes for what is on the player's back first.</summary>
        public double Loyalty;
        /// <summary>0 says nothing, 1 talks.</summary>
        public double Talk;
        /// <summary>When it may speak again, and when it may say something of its own.</summary>
        public double SayAt, AmbientAt;
        /// <summary>The enemy it last called out, so one rare is warned about once.</summary>
        public uint Warned;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, ControlledMind> ControlledMinds = new();

    internal static ControlledMind MindOf(Player conn)
    {
        if (ControlledMinds.TryGetValue(conn, out var known)) return known;
        uint seed = ControlledSeed(conn);
        var mind = new ControlledMind
        {
            Charge = ((seed >> 1) & 0xff) / 255.0,
            Caution = ((seed >> 9) & 0xff) / 255.0,
            Loyalty = ((seed >> 17) & 0xff) / 255.0,
            Talk = ((seed >> 24) & 0xff) / 255.0,
        };
        ControlledMinds.Add(conn, mind);
        return mind;
    }

    /// <summary>FNV-1a of the character's name: the same bot is the same person in every area, login and reload.</summary>
    internal static uint ControlledSeed(Player conn)
    {
        uint hash = 2166136261;
        foreach (char c in Program.CharacterName(conn)) { hash ^= c; hash *= 16777619; }
        return hash;
    }

    /// <summary>How far from the player this bot will go after an enemy: the cautious keep to its side, the bold range the
    /// whole sight. Never past the engage leash (ControlledFollowing), which is what the follower recovers from.</summary>
    internal static double ControlledLeaderRange(Player conn) => 55 + 32 * MindOf(conn).Charge;

    /// <summary>The enemies the other bots here are already fighting: plain monsters are divided between them, rares and
    /// bosses are not (a party does pile onto those). A bot is not in Social's party list, so its companions are the other
    /// controlled players in the same area. Read outside the monster list's lock.</summary>
    internal static HashSet<uint> ControlledClaimedIds(Player conn, double now)
    {
        var claimed = new HashSet<uint>();
        foreach (var other in Program.InGame.Keys)
        {
            if (ReferenceEquals(other, conn) || other.HasClient) continue;
            if (!ReferenceEquals(other.Instance.Presence, conn.Instance.Presence)) continue;
            if (!FollowerPlans.TryGetValue(other, out var plan) || plan.Target == 0) continue;
            if (now < plan.AttackUntil + 1.5) claimed.Add(plan.Target);
        }
        return claimed;
    }

    /// <summary>What an enemy is worth to this bot: the rarity first, then whether it is on the player's back (a loyal bot
    /// turns on those) or on the bot's own. The caller breaks ties by distance.</summary>
    internal static int ControlledWorth(Player conn, SpawnedMonster monster, Player leader)
    {
        var mind = MindOf(conn);
        int worth = (int)monster.Rarity * 20;
        if (ReferenceEquals(monster.Target, leader)) worth += 45 + (int)(30 * mind.Loyalty);
        else if (ReferenceEquals(monster.Target, conn)) worth += 20;
        return worth;
    }

    /// <summary>Give ground, as a player kites: a ranged bot steps out of a crowd that got inside its reach, more readily the
    /// more cautious it is. Stays inside the party (the engage leash) and off the walls. A melee bot stands and drinks.</summary>
    internal static bool ControlledBackOff(Player conn, Player leader, (int X, int Y) enemy, double gap, double reach, double now)
    {
        var plan = FollowerPlans.GetOrCreateValue(conn);
        var mind = MindOf(conn);
        double life = conn.PlayerMaxLife == 0 ? 1 : conn.PlayerLife / (double)conn.PlayerMaxLife;
        // Only a bot that fights from a distance has somewhere to back off to; a melee one would just stop fighting. An enemy
        // right on top of it is what it steps away from, not one at the edge of its reach.
        bool pressed = reach >= 25 && gap < Math.Min(14, Math.Max(6, reach * .25));
        bool hurt = reach >= 25 && life < .12 + .18 * mind.Caution;
        if (!pressed && !hurt) { plan.Backing = false; return false; }
        if (now < plan.KiteAt) return plan.Backing;
        plan.KiteAt = now + 1.1;
        var here = conn.Walk.Position(now);
        double dx = here.X - enemy.X, dy = here.Y - enemy.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < .001) return plan.Backing = false;
        double step = hurt ? 12 : 8;
        foreach (double turn in new[] { 0.0, .6, -.6, 1.2, -1.2 })
        {
            double angle = Math.Atan2(dy, dx) + turn;
            var at = (X: here.X + (int)Math.Round(Math.Cos(angle) * step), Y: here.Y + (int)Math.Round(Math.Sin(angle) * step));
            if (!ControlledMoveFits(conn, leader, at.X, at.Y, now, ControlledEngageLeash)) continue;
            MoveControlledFollower(conn, at, now);
            return plan.Backing = true;
        }
        return plan.Backing = false;
    }

    /// <summary>One line from the bot in the area's local chat. Rate-limited per bot: five followers talking at every pack
    /// is worse than silence, so a bot speaks at most once a minute and an event that keeps happening waits longer. The line
    /// goes to the people in the area, as a player's local chat does (a bot is not in Social's party list).</summary>
    internal static void ControlledSay(Player conn, double now, string text, double gap = 75)
    {
        var mind = MindOf(conn);
        if (now < mind.SayAt || conn.Closed) return;
        string name = Program.CharacterName(conn);
        bool told = false;
        foreach (var other in Program.InGame.Keys)
        {
            if (other is not { HasClient: true, Closed: false, Connection.Outbox: { } outbox }) continue;
            if (!ReferenceEquals(other.Instance.Presence, conn.Instance.Presence)) continue;
            // A send encrypts in place, so each client gets its own bytes (ChatModule.SayLocally). A client that has gone
            // leaves a faulted send behind, which is dropped rather than raised on the bots' loop.
            var line = outbox(InstanceChat.ChatLine(text, name), $"0x0017 chat line from {name}");
            line.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            told = true;
        }
        if (!told) return;
        mind.SayAt = now + gap;
        Console.WriteLine($"[bots] {name} says {text}");
    }

    /// <summary>Calls an enemy out once, and only by a bot that talks at all: "a rare!" from five followers is noise.</summary>
    internal static void ControlledWarn(Player conn, double now, uint id, string text, double gap = 90)
    {
        var mind = MindOf(conn);
        if (mind.Talk < .6 || mind.Warned == id) return;
        mind.Warned = id;
        ControlledSay(conn, now, text, gap);
    }

    // Short lines for standing around, in the voice of someone travelling with the player rather than narrating.
    static readonly string[] ControlledSmallTalk =
    [
        "这地方怪不少。",
        "跟紧我，别走散了。",
        "我这边清完了。",
        "下一站在哪？",
        "等我喝口药。",
        "你打前面，我守后面。",
    ];

    /// <summary>Something to say while nothing is happening: only a talkative bot does, and rarely.</summary>
    internal static void ControlledSmallTalkAt(Player conn, double now)
    {
        var mind = MindOf(conn);
        if (mind.Talk < .35 || now < mind.AmbientAt) return;
        mind.AmbientAt = now + 150 + 180 * mind.Talk;
        uint pick = ControlledSeed(conn) + (uint)(now * 7);
        ControlledSay(conn, now, ControlledSmallTalk[pick % ControlledSmallTalk.Length], gap: 60);
    }
}
