using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

using static Game;

namespace GameServer.Modules.PlayerBots;

public static partial class BotPlay
{
    // ---- a party bot's own decisions in a fight ----
    // Everything here runs only for a controlled player (ControlledPlayers), never for a human's connection: which enemy
    // to hit, with which gem, whether to close the distance, and when to drink. A follower used to take the first gem that
    // could hit at all and only ever hit what was already in its reach, so it stood still while a rare killed the leader
    // three cells away (live 2026-11-09: "not smart, like an idiot").

    /// <summary>A gem the follower can bring to bear, with what aiming it takes: the reach the server itself uses for a
    /// hit in place (<see cref="ControlledInPlaceReach"/>), whether it covers an area, how long its use occupies the
    /// character (the follower may not move mid-swing), and what it costs.</summary>
    internal sealed record ControlledSkill(ushort Id, int Effect, int Level, int Quality, double Reach, bool Area, double Seconds, int Mana);

    /// <summary>How far a bot fights at: its own reach, and with it what it sees an enemy in (live 2026-11-10: "make the
    /// bots' attack distance 80 cells"). A follower used to stop at 60 of the leader, so a fight the player started from
    /// the far side of a room left it standing.</summary>
    const double ControlledSight = 80;
    /// <summary>A monster within this many cells of the target means there is a pack, so an area gem is worth it.</summary>
    const double ControlledPackRange = 15;
    const int ControlledPackSize = 3;

    internal static List<ControlledSkill> ControlledSkills(Player conn)
    {
        var list = new List<ControlledSkill>();
        foreach (var gem in GemSkillEffects(conn))
        {
            if (GemSkillInstanceDisabled(conn, gem.Effect, gem.Id) is not null) continue;
            if (!SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality).CanHit) continue;
            // Reactive reservation hits do not make their toggles usable as automatic attacks: a Herald or an aura that
            // can hit on a condition is still a toggle, and a bot that "cast" it would switch it off (upstream fix).
            if (ToggledReservation(conn, gem.Id) is not null) continue;
            double seconds = SkillsFor(conn).IsAttack(gem.Effect)
                ? SupportedAttackSeconds(conn, gem.Id) : (CastMs(conn, gem.Effect, gem.Id) ?? 750) / 1000.0;
            var supported = SupportedSkill(conn, gem.Id);
            bool area = SkillRadius(conn, supported.Stats, SkillsFor(conn).IsSpell(gem.Effect), gem.Effect) >= 12;
            // What a use costs: a bot with 3 mana cannot pay a 12-mana Cleave, and stood there instead (ControlledPlayers
            // falls back on the weapon's own attack then).
            int mana = supported.Stats.GetValueOrDefault(Stat.ManaCost);
            list.Add(new(gem.Id, gem.Effect, gem.Level, gem.Quality,
                ControlledInPlaceReach(conn, gem.Id, gem.Effect), area, seconds, mana));
        }
        return list;
    }

    /// <summary>The enemy this bot picks for itself: what is worth most first (a rare, then whatever is on the player's back
    /// or its own), then the nearest. Null when nothing hostile stands inside its sight and inside how far it will go from
    /// the leader (<see cref="ControlledLeaderRange"/>). It keeps off an enemy another bot is already fighting where it can,
    /// so a party spreads over a pack instead of piling onto one skeleton.</summary>
    internal static (uint Id, SpawnedMonster Monster)? ControlledTarget(Player conn, Player leader, (int X, int Y) here, double now)
    {
        var leaderAt = leader.Walk.Position(now);
        double leaderRange = ControlledLeaderRange(conn);
        // Read the other bots' targets before taking the monster list's lock: the party list has a lock of its own, and the
        // two must never be held in both orders.
        var claimed = ControlledClaimedIds(conn, now);
        lock (conn.Area.Monsters)
        {
            var seen = conn.Area.Monsters
                .Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && Dist(p.Value.Walk.Position(now), here) <= ControlledSight
                    && Dist(p.Value.Walk.Position(now), leaderAt) <= leaderRange)
                .Select(p => (Id: p.Key, Monster: p.Value, At: p.Value.Walk.Position(now)))
                .ToList();
            if (seen.Count == 0) return null;
            var unclaimed = seen.Where(s => s.Monster.Rarity >= MonsterRarity.Rare || !claimed.Contains(s.Id)).ToList();
            var pool = unclaimed.Count > 0 ? unclaimed : seen;
            return pool
                .OrderByDescending(s => ControlledWorth(conn, s.Monster, leader))
                .ThenBy(s => Dist(s.At, here))
                .Select(s => ((uint Id, SpawnedMonster Monster)?)(s.Id, s.Monster))
                .FirstOrDefault();
        }
    }

    /// <summary>Whether several monsters stand around this one: an area gem hits them all.</summary>
    internal static bool ControlledPacked(Player conn, SpawnedMonster target, double now)
    {
        var at = target.Walk.Position(now);
        lock (conn.Area.Monsters)
            return conn.Area.Monsters.Count(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Asleep: false }
                && Dist(p.Value.Walk.Position(now), at) <= ControlledPackRange) >= ControlledPackSize;
    }

    /// <summary>The gems that reach that distance, in the order they are socketed, with an area gem first when a pack stands
    /// there. Empty when nothing reaches: the caller closes the distance.</summary>
    internal static List<ControlledSkill> ControlledSkillsFor(List<ControlledSkill> skills, double distance, bool packed)
    {
        var reaching = skills.Where(s => distance <= s.Reach).ToList();
        if (packed && reaching.FirstOrDefault(s => s.Area) is { } area)
        {
            reaching.Remove(area);
            reaching.Insert(0, area);
        }
        return reaching;
    }

    /// <summary>Whether this gem can be used now: the next gem in the list is used meanwhile, where the follower used to
    /// stand on a cooling skill doing nothing. Reading the state also refreshes it, so it happens inside the same short
    /// critical section the game's own cooldown path takes (<see cref="ProjectileMutation"/>) rather than under the
    /// monster list's lock, which guards a different table.</summary>
    internal static bool ControlledSkillReady(Player conn, ControlledSkill skill, double now)
    {
        if (CooldownFor(conn, skill.Effect, skill.Level, skill.Quality) is not { } plan) return true;
        using (ProjectileMutation(conn, advance: false))
            return CooldownStateLocked(conn, skill.Effect, plan, now).Ready;
    }

    /// <summary>Walk to just inside the reach of that enemy. <see cref="ControlledMoveFits"/> keeps the destination inside
    /// the leader's leash and on walkable ground, so chasing never leaves the party or walks through a wall. The leash is the
    /// engage one: closing on a target at the edge of the bot's sight would otherwise be refused as too far from the player.</summary>
    internal static bool ApproachControlledTarget(Player conn, Player leader, (int X, int Y) monster, double reach, double now)
    {
        var plan = FollowerPlans.GetOrCreateValue(conn);
        if (now < plan.ApproachAt) return plan.Approaching;
        plan.ApproachAt = now + .5;
        var here = conn.Walk.Position(now);
        double dx = here.X - monster.X, dy = here.Y - monster.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < .001) return plan.Approaching = false;
        // A cell short of the reach, on the side it already stands: no walking into the monster.
        int step = Math.Max(2, (int)Math.Round(Math.Max(2, reach) - 3));
        var at = (X: monster.X + (int)Math.Round(dx / length * step), Y: monster.Y + (int)Math.Round(dy / length * step));
        plan.Approaching = ControlledMoveFits(conn, leader, at.X, at.Y, now, ControlledEngageLeash);
        if (!plan.Approaching) return false;
        MoveControlledFollower(conn, at, now);
        return true;
    }

    /// <summary>Drink the first flask of the bar that still has charges: a life one under this bot's own line (a cautious
    /// one drinks at 40% of its life, a bold one at 20%), a mana one when casting ran it dry. False when there is nothing to
    /// drink, the bar is empty or it drank a moment ago.</summary>
    internal static async Task<bool> DrinkControlledFlask(Player conn, double now)
    {        if (conn.PlayerLife == 0 || conn.PlayerMaxLife == 0) return false;
        double below = .2 + .2 * MindOf(conn).Caution;
        bool life = conn.PlayerLife / (double)conn.PlayerMaxLife <= below;
        int mana = UnreservedMana(conn);
        bool dry = mana > 0 && conn.PlayerMana * 5 < mana;
        if (!life && !dry) return false;
        var plan = FollowerPlans.GetOrCreateValue(conn);
        if (now < plan.FlaskAt) return false;
        foreach (var held in ItemsOf(conn).Items(PlayerInventories.Flask))
        {
            if (ItemsFor(conn).ByHash(held.Item.TypeHash) is not { } type) continue;
            if (ItemsFor(conn).FlaskOf(type) is not { } use) continue;
            if (held.Item.Charges < use.ChargesPerUse) continue;
            if (life ? use.LifePerUse <= 0 : use.ManaPerUse <= 0) continue;
            plan.FlaskAt = now + 1.2;
            await UseFlask(conn, held.X);
            Console.WriteLine($"[bots] {Program.CharacterName(conn)} drinks a {(life ? "life" : "mana")} flask: " +
                $"{conn.PlayerLife}/{conn.PlayerMaxLife} life, {conn.PlayerMana}/{mana} mana");
            return true;
        }
        return false;
    }
}
