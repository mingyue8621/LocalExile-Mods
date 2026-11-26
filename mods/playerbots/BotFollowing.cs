using GameServer.Core.World;

using static Game;

namespace GameServer.Modules.PlayerBots;

// How a bot plays: the game's own rules, run for a player the server drives.
public static partial class BotPlay
{

    /// <summary>How far behind it will still try to run after the leader before blinking back into the party: it keeps to
    /// the leader's side unless it is fighting (ControlledEngageLeash).</summary>
    public const double ControlledFollowerLeash = 60;
    /// <summary>How far it may get from the leader while it is fighting: its 80-cell reach is measured from itself, so a
    /// target at the edge of that would otherwise be untouchable without stepping outside the follow leash.</summary>
    internal const double ControlledEngageLeash = 70;
    /// <summary>How far behind it will still try to run after the leader before blinking back into the party.</summary>
    internal const double ControlledCatchUpRange = 120;
    /// <summary>How long it keeps running while the gap is not closing; the leader's own speed means it may never close.</summary>
    internal const double ControlledCatchUpSeconds = 1.5;

    static readonly Store<Player, ControlledFollowing> FollowerPlans = new();
    sealed class ControlledFollowing
    {
        public bool Following;
        public double RepathAt, AttackUntil, IdleAt = double.NaN, FarSince = double.NaN;
        /// <summary>When this follower last logged that it found nowhere to stand (so the log says why it is stuck).</summary>
        public DateTime NoSpotLogged;
        /// <summary>When it last logged why it is still far behind the leader.</summary>
        public DateTime DiagLogged;
        // The fight's own timing: when it may look for a new approach spot (and whether the last one was walkable), and when
        // it may drink again (ControlledCombat).
        public double ApproachAt, FlaskAt;
        public bool Approaching;
        // The catch-up run after a leader that got away: when it started and how near it got (ControlledFollowing).
        public double CatchUpAt, CatchUpBest;
        public (int X, int Y) LeaderAt, LeaderGoal;
        // A fight widens the leash (ControlledEngageLeash) until this passes; a bot that gives ground (ControlledBackOff)
        // may do so again after KiteAt, and Backing says it stepped away rather than failed to find a cell.
        public double EngageUntil, KiteAt;
        public bool Backing;
        /// <summary>When it may step off the leader's route again (a follower that ended up ahead of it).</summary>
        public double StepAsideAt;
        public uint IdleTurn;
        public uint Target;
    }

    /// <summary>The cell this follower forms up on: behind the leader, off the line it is walking down, and never on top of
    /// it. A follower used to take a cell <em>ahead</em> of a moving leader (18–30 cells along its route), so five of them
    /// made a wall the player had to walk round (live 2026-11-10: "the bots surround me and block my path").</summary>
    public static (int X, int Y) ControlledFollowerPoint(Player player, Player leader, bool arrival = false, double? atTime = null)
    {
        double now = atTime ?? AreaTime(player);
        var origin = leader.Walk.Position(now);
        var destination = leader.Walk.Destination;
        double dx = destination.X - origin.X, dy = destination.Y - origin.Y;
        bool moving = leader.Walk.Moving(now) && (dx != 0 || dy != 0);
        // A destination the player has not reached still says which way they were walking, so a stopped player keeps the
        // formation behind them instead of letting the followers drift round to the front.
        bool heading = moving || dx != 0 || dy != 0;
        // Which way the formation lies: backwards along a walk, away from the doorway it followed the leader through, and
        // otherwise the side it already stands on — a follower keeps its side of the leader instead of circling him.
        var here = player.Walk.Position(now);
        double outward = Math.Atan2(here.Y - origin.Y, here.X - origin.X);
        if (heading) outward = Math.Atan2(dy, dx) + Math.PI;
        else if (arrival)
        {
            var entry = player.Layout.AreaTransitions.Select(p => p.Transition)
                .OrderBy(t => Dist(origin, (t.GridX, t.GridY))).FirstOrDefault();
            if (entry is not null && Dist(origin, (entry.GridX, entry.GridY)) < 50)
                outward = Math.Atan2(origin.Y - entry.GridY, origin.X - entry.GridX);
        }
        // A cell inside the corridor the leader is walking down is a body its own pathfinder has to go round.
        double length = Math.Sqrt(dx * dx + dy * dy);
        bool OnRoute(int x, int y)
        {
            if (!moving || length < 1) return false;
            double ux = dx / length, uy = dy / length, ax = x - origin.X, ay = y - origin.Y;
            double along = ax * ux + ay * uy;
            // A cell inside the corridor the leader is walking down is a body its own pathfinder has to go round. The lane
            // is wider and longer than the leader is: a follower at its edge still reads as standing in the way.
            return along > -4 && along < 30 && Math.Abs(ay * ux - ax * uy) < 7;
        }
        // Try the trail first, then either side of a narrow passage. Bodies are live inputs.
        double side = (player.Id % 5 - 2.0) * .3;
        // Each bot prefers its own rank in the trail, so three of them walk at three distances instead of shoulder to
        // shoulder: the queue behind a player is a column, not a rank.
        int rank = (int)(player.Id % 3);
        // Far enough that a body is never under the player's feet: a follower at 7 cells is in the way the moment the
        // player sets off, and in a doorway there is no room to walk round one. The distances still form a column.
        int[] radii = moving ? [12, 16, 20] : [14, 18, 22];
        var bodies = player.Blockers;
        bool Floor(int x, int y) => x >= 0 && y >= 0 && x < player.Layout.GridW && y < player.Layout.GridH
            && player.Layout.WalkableFor(x, y, 2) && !player.Barrier.Contains((x, y));
        bool TryPick(int[] tryRadii, double[] turns, bool avoidRoute, out (int X, int Y) pick)
        {
            for (int i = 0; i < tryRadii.Length; i++)
            {
                int radius = tryRadii[(i + rank) % tryRadii.Length];
                foreach (double turn in turns)
                {
                    double angle = outward + side + turn;
                    var at = (X: origin.X + (int)Math.Round(Math.Cos(angle) * radius),
                        Y: origin.Y + (int)Math.Round(Math.Sin(angle) * radius));
                    if ((avoidRoute && OnRoute(at.X, at.Y)) || !Floor(at.X, at.Y)
                        || ClientPathfinder.ObjectBlocks(bodies, at.X, at.Y, 3, 6)) continue;
                    var path = ClientPathfinder.FindPath(origin, at, Floor, player.Layout.GridW, player.Layout.GridH, [], 2);
                    if (path.Count > 1 && path[^1] == at) { pick = at; return true; }
                }
            }
            pick = (here.X, here.Y);
            return false;
        }
        // The formation it would like, then nearer cells, and only as a last resort cells the leader's own pathfinder may
        // have to go round. A follower that finds no spot at all stands still, which is exactly what reads as it having
        // stopped following (live 2026-11-11: after a pack, in a doorway, it never moved again).
        if (TryPick(radii, [0.0, .45, -.45, .8, -.8], true, out var chosen)) return chosen;
        if (TryPick([9, 7, 11], [0.0, .4, -.4, .8, -.8], true, out chosen)) return chosen;
        if (TryPick([9, 12, 7], [0.0, .45, -.45, .9, -.9], false, out chosen)) return chosen;
        // Nowhere at all: say so, once every few seconds, rather than looking like a follower that simply gave up.
        var plans = FollowerPlans.GetOrCreateValue(player);
        if (DateTime.UtcNow - plans.NoSpotLogged > TimeSpan.FromSeconds(5))
        {
            plans.NoSpotLogged = DateTime.UtcNow;
            Console.WriteLine($"[bots] {Program.CharacterName(player)} found no cell to stand in near ({origin.X}, {origin.Y}): "
                + $"it stands at ({here.X}, {here.Y}), {Dist(here, origin):0.0} cells behind");
        }
        return here;
    }

    public static bool KeepControlledFollowerNear(Player player, Player leader, double now, bool externalMovement = false)
    {
        var plan = FollowerPlans.GetOrCreateValue(player);
        var here = player.Walk.Position(now);
        var follow = leader.Walk.Position(now);
        double distance = Dist(here, follow);
        // Far behind: say what holds it there, once every few seconds. Without this a stuck follower leaves nothing in the
        // log at all, and "it stopped following" cannot be told apart from "it never tried".
        if (distance > 40 && DateTime.UtcNow - plan.DiagLogged > TimeSpan.FromSeconds(5))
        {
            plan.DiagLogged = DateTime.UtcNow;
            Console.WriteLine($"[bots] {Program.CharacterName(player)} diag: gap {distance:0.0} cells, leader moving {leader.Walk.Moving(now)}, "
                + $"following {plan.Following}, engage {now < plan.EngageUntil}, attacking {now < plan.AttackUntil}, "
                + $"walking {player.Walk.Moving(now)}");
        }
        // A bot in a fight is allowed the engage leash: it is 80 from itself to its target, and that target may stand a
        // little away from the player. Once the fight is over the party tightens again.
        double leash = now < plan.EngageUntil ? ControlledEngageLeash : ControlledFollowerLeash;
        if (distance > leash && double.IsNaN(plan.FarSince)) plan.FarSince = now;
        if (distance <= leash) plan.FarSince = double.NaN;
        // Brief gaps are caught on foot; a blink or an obstructed follower cannot stay off screen. The blink distance keeps
        // clear of the leash, so a fight at its edge never reads as the follower teleporting back.
        if (distance > leash + 40 || distance > leash && now - plan.FarSince >= 1.25)
        {
            // Run after the leader before blinking: a follower appearing out of nowhere beside the player is what reads as
            // teleporting. While the gap keeps closing it keeps running; when it stops closing (the leader runs on at the
            // same speed, or the way is blocked) it comes back the old way.
            if (distance + .5 < plan.CatchUpBest) { plan.CatchUpBest = distance; plan.CatchUpAt = now; }
            else if (plan.CatchUpAt == 0) { plan.CatchUpAt = now; plan.CatchUpBest = distance; }
            if (distance <= ControlledCatchUpRange && now - plan.CatchUpAt < ControlledCatchUpSeconds)
            {
                if (now >= plan.RepathAt)
                {
                    MoveControlledFollower(player, ControlledFollowerPoint(player, leader, atTime: now), now);
                    plan.RepathAt = now + .5;
                }
                plan.Following = true;
                return true;
            }
            plan.CatchUpAt = 0;
            plan.CatchUpBest = 0;
            var at = ControlledFollowerPoint(player, leader, atTime: now);
            Interlocked.Increment(ref player.ActionCount);
            Interlocked.Increment(ref player.ChannelGeneration);
            player.ChannelSkill = 0;
            player.Walk.Teleport(at.X, at.Y, now);
            BodyOf(player).NextAction = now + .35;
            plan.Following = false;
            plan.AttackUntil = 0;
            plan.Target = 0;
            plan.FarSince = double.NaN;
            ScheduleControlledIdle(player, plan, now);
            PublishPlayerPose(player);
            Console.WriteLine($"[bots] {Program.CharacterName(player)} returns to leader: {distance:0.0} cells -> ({at.X}, {at.Y})");
            return true;
        }
        // Hysteresis: a moving leader is followed in legs, not by constantly finding a new formation cell.
        // A bot in a fight holds its ground instead: it picked an enemy inside its own 80-cell reach, and walking to the
        // leader's formation cell would take it out of that reach. The catch-up above still brings it back once the player
        // is past the engage leash.
        if (now < plan.EngageUntil && distance <= leash && !externalMovement)
        {
            plan.Following = false;
            return false;
        }
        bool moving = leader.Walk.Moving(now);
        double ahead = ControlledAhead(here, follow, leader.Walk.Destination);
        // Ahead of the leader on its own route: it steps off the road and waits beside it, rather than standing in the
        // corridor (a body the player has to walk round) or running back through him.
        if (moving && distance <= leash && ahead >= 4 && Math.Abs(LateralFromRoute(here, follow, leader.Walk.Destination)) < 5)
        {
            // Already ahead on the route: wait for the leader rather than running backwards to a formation point.
            if (plan.Following) ScheduleControlledIdle(player, plan, now);
            plan.Following = false;
            StepOffRoute(player, leader, now);
            return false;
        }
        if (plan.Following && !moving && distance <= 30)
        {
            plan.Following = false;
            ScheduleControlledIdle(player, plan, now);
        }
        if (moving || distance > leash) plan.Following = true;
        if (externalMovement && distance <= leash) return plan.Following;
        if (!plan.Following) return false;
        if (now < plan.AttackUntil) return true;
        if (now >= plan.RepathAt && (!player.Walk.Moving(now) || Dist(plan.LeaderAt, follow) >= 12
            || Dist(plan.LeaderGoal, leader.Walk.Destination) >= 12))
        {
            MoveControlledFollower(player, ControlledFollowerPoint(player, leader, atTime: now), now);
            plan.LeaderAt = follow;
            plan.LeaderGoal = leader.Walk.Destination;
            plan.RepathAt = now + .75;
        }
        return true;
    }

    static void ScheduleControlledIdle(Player player, ControlledFollowing plan, double now)
    {
        uint seed = unchecked(player.Id * 2654435761u + plan.IdleTurn * 2246822519u);
        plan.IdleAt = now + 14 + seed % 18;
    }

    public static void IdleControlledFollower(Player player, Player leader, double now)
    {
        var plan = FollowerPlans.GetOrCreateValue(player);
        plan.Target = 0;
        if (double.IsNaN(plan.IdleAt)) ScheduleControlledIdle(player, plan, now);
        if (now < plan.IdleAt || now < plan.AttackUntil || player.Walk.Moving(now) || leader.Walk.Moving(now)) return;
        uint turn = ++plan.IdleTurn;
        ScheduleControlledIdle(player, plan, now);
        // Most idle decisions leave the bot standing. A rare adjustment stays by its own feet.
        if (turn % 3 != 1) return;
        uint seed = unchecked(player.Id * 2654435761u + turn * 2246822519u);
        var here = player.Walk.Position(now);
        var bodies = player.Blockers;
        bool Floor(int x, int y) => x >= 0 && y >= 0 && x < player.Layout.GridW && y < player.Layout.GridH
            && player.Layout.WalkableFor(x, y, 2) && !player.Barrier.Contains((x, y));
        var at0 = leader.Walk.Position(now);
        var destination = leader.Walk.Destination;
        double bx = destination.X - at0.X, by = destination.Y - at0.Y;
        // An idle shuffle, not the follow pass: a short step from its own feet, and while the player is walking never one
        // that puts it in front of them (an idle follower that wanders round to the front is the one that blocks a doorway
        // the moment the player walks on). Standing still there is no front to keep clear, and it shuffles anywhere.
        bool walking = bx != 0 || by != 0;
        double length = walking ? Math.Sqrt(bx * bx + by * by) : 0;
        double fx = walking ? bx / length : 0, fy = walking ? by / length : 0;
        for (int trial = 0; trial < 8; trial++)
        {
            double angle = (seed % 628) / 100.0 + trial * Math.PI / 4;
            int radius = 4 + (int)((seed + (uint)trial * 7919u) % 5);
            var at = (X: here.X + (int)Math.Round(Math.Cos(angle) * radius),
                Y: here.Y + (int)Math.Round(Math.Sin(angle) * radius));
            // A radius-three diagonal can round below the move admission's minimum distance. Keep trying instead
            // of consuming this idle decision on a destination which MoveControlledFollower will refuse.
            if (Dist(here, at) < 3) continue;
            if (Dist(at, leader.Walk.Position(now)) > ControlledFollowerLeash || !Floor(at.X, at.Y)
                || ClientPathfinder.ObjectBlocks(bodies, at.X, at.Y, 3, 6)) continue;
            // Never onto the player's own cell or beside it: a follower at arm's length is in the way the moment the
            // player turns.
            if (Dist(at, at0) < 3) continue;
            if (walking && (at.X - at0.X) * fx + (at.Y - at0.Y) * fy > 0) continue;
            var path = ClientPathfinder.FindPath(here, at, Floor, player.Layout.GridW, player.Layout.GridH, bodies, 2);
            if (path.Count < 2 || path[^1] != at) continue;
            // Rounded path cells can briefly leave the circle even with both endpoints inside it.
            var origin = leader.Walk.Position(now);
            if (path.Zip(path.Skip(1)).Any(p => ClientPathfinder.Line(p.First, p.Second)
                .Any(cell => Dist(cell, origin) > ControlledFollowerLeash))) continue;
            if (MoveControlledFollower(player, at, now)) break;
        }
    }

    public static double ControlledAhead((int X, int Y) bot, (int X, int Y) leader, (int X, int Y) goal)
    {
        double dx = goal.X - leader.X, dy = goal.Y - leader.Y, length = Math.Sqrt(dx * dx + dy * dy);
        return length > 0 ? ((bot.X - leader.X) * dx + (bot.Y - leader.Y) * dy) / length : 0;
    }


    /// <summary>How far to the side of the leader's route a cell stands: one sign on one side, the other on the other.</summary>
    internal static double LateralFromRoute((int X, int Y) cell, (int X, int Y) from, (int X, int Y) goal)
    {
        double dx = goal.X - from.X, dy = goal.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return 0;
        return ((cell.X - from.X) * -dy + (cell.Y - from.Y) * dx) / length;
    }

    /// <summary>Steps off the road the leader is walking down, onto the side it already stands on, and waits there: a body
    /// in the corridor is what the player's pathfinder has to walk round.</summary>
    static void StepOffRoute(Player player, Player leader, double now)
    {
        var plan = FollowerPlans.GetOrCreateValue(player);
        if (now < plan.StepAsideAt) return;
        plan.StepAsideAt = now + 1.2;
        var here = player.Walk.Position(now);
        var from = leader.Walk.Position(now);
        var goal = leader.Walk.Destination;
        double dx = goal.X - from.X, dy = goal.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        double side = LateralFromRoute(here, from, goal) >= 0 ? 1 : -1;
        var at = (X: here.X + (int)Math.Round(-dy / length * side * 7),
            Y: here.Y + (int)Math.Round(dx / length * side * 7));
        if (!ControlledMoveFits(player, leader, at.X, at.Y, now)) return;
        MoveControlledFollower(player, at, now);
    }

    /// <summary>Whether stopping to swing while the leader walks on still leaves the bot in the party afterwards: it may do
    /// so from further out now that it fights at 80, but not from so far that the player is gone by the end of the swing.</summary>
    public static bool ControlledFollowAttackFits(double gap, double leaderSpeed, double seconds, double ahead = 0)

    {
        double after = ahead - Math.Max(0, leaderSpeed) * seconds;
        return gap <= 65 && Math.Max(0, gap * gap - ahead * ahead) + after * after <= 70 * 70;
    }

    public static void HoldControlledFollowerAttack(Player player, double now, double seconds, uint target)
    {
        var plan = FollowerPlans.GetOrCreateValue(player);
        plan.Target = target;
        plan.AttackUntil = now + Math.Max(.35, seconds) + .1;
        BodyOf(player).NextAction = plan.AttackUntil;
        plan.RepathAt = plan.AttackUntil;
        ScheduleControlledIdle(player, plan, plan.AttackUntil);
    }

    static bool MoveControlledFollower(Player player, (int X, int Y) at, double now)
    {
        if (player.Walk.Moving(now) && Dist(player.Walk.Destination, at) < 6) return false;
        if (Dist(player.Walk.Position(now), at) < 3) return false;
        Interlocked.Increment(ref player.ActionCount);
        Interlocked.Increment(ref player.ChannelGeneration);
        player.ChannelSkill = 0;
        player.Walk.Order(at.X, at.Y, now);
        PublishPlayerPose(player);
        return true;
    }
}
