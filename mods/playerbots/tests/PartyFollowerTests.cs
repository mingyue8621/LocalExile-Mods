using GameServer.Modules.PlayerBots;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class PartyFollowerTests
{
    [Fact]
    public async Task Followers_clear_the_entrance_remain_calm_and_balance_attacking_with_catching_up()
    {
        ControlledPlayerBuildChecks.VerifyCampaignBuilds();
        using var runtime = new Runtime();
        using var wire = new Wire();
        var instance = new InstanceState();
        var leader = runtime.PlayerOf(instance, wire);
        var bot = runtime.PlayerOf(instance, wire);
        leader.Walk.Teleport(100, 100, 0);
        leader.Walk.Order(180, 100, 0);
        var ahead = BotPlay.ControlledFollowerPoint(bot, leader, arrival: true);
        // Behind the leader, off the line it is walking down: a follower used to take a cell ahead of it, and five of them
        // made a wall the player's own pathfinder had to walk round (live 2026-11-10).
        Assert.True(ahead.X < 95);
        // The trail radii rotate with the follower's rank (PeerId % 3 over [12, 16, 20]) and each cell is rounded, so a
        // radius-20 spot lands up to about 20.7 cells out: the bound covers the rounding, not a follower walking further.
        Assert.InRange(Math.Sqrt(Math.Pow(ahead.X - 100, 2) + Math.Pow(ahead.Y - 100, 2)), 6, 21);
        var runner = runtime.PlayerOf(instance, wire);
        runner.Walk.Teleport(201, 100, 0);
        BotPlay.BodyOf(runner).NextAction = 1000;
        Assert.True(BotPlay.KeepControlledFollowerNear(runner, leader, 0));
        // A gap over the leash is closed on foot first: the order is a walk back toward the trail, not a jump beside the player.
        Assert.Equal((201, 100), runner.Walk.Position(0));
        Assert.True(Game.Dist(runner.Walk.Destination, (100, 100)) < 32);
        Assert.Equal(1000, BotPlay.BodyOf(runner).NextAction);
        // Too far to walk back at all, it comes back at once, and behind the leader.
        var stranded = runtime.PlayerOf(instance, wire);
        stranded.Walk.Teleport(300, 300, 0);
        Assert.True(BotPlay.KeepControlledFollowerNear(stranded, leader, 0));
        Assert.True(Game.Dist(stranded.Walk.Position(0), (100, 100)) < 32);
        Assert.Equal(.35, BotPlay.BodyOf(stranded).NextAction);
        var trailer = runtime.PlayerOf(instance, wire);
        trailer.Walk.Teleport(180, 100, 0);
        int action = trailer.ActionCount;
        Assert.True(BotPlay.KeepControlledFollowerNear(trailer, leader, 0));
        Assert.Equal((180, 100), trailer.Walk.Position(0)); // Normal catch-up walks, rather than teleports.
        Assert.True(trailer.ActionCount > action);
        Assert.True(trailer.Walk.Destination.X < 100);   // Back onto the trail behind the leader, not further ahead of it.
        Assert.True(Game.Dist(trailer.Walk.Destination, (100, 100)) < 32);
        // A nearby swing/cast gets its contact before following resumes, rather than being cancelled every AI tick.
        trailer.Walk.Teleport(70, 100, 0);
        trailer.Walk.Order(70, 100, 0);
        BotPlay.HoldControlledFollowerAttack(trailer, 0, .75, 123);
        action = trailer.ActionCount;
        Assert.True(BotPlay.KeepControlledFollowerNear(trailer, leader, .2));
        Assert.Equal(action, trailer.ActionCount);
        Assert.Equal((70, 100), trailer.Walk.Destination);
        Assert.True(BotPlay.KeepControlledFollowerNear(trailer, leader, 1));
        Assert.True(trailer.ActionCount > action);
        Assert.True(BotPlay.ControlledFollowAttackFits(20, 37, .75, ahead: -20));
        Assert.True(BotPlay.ControlledFollowAttackFits(30, 40, .75, ahead: 30));
        Assert.True(BotPlay.ControlledFollowAttackFits(60, 37, .75));   // fights from further out now that it does at 80
        Assert.False(BotPlay.ControlledFollowAttackFits(70, 0, .1));    // but not from so far that the player is gone
        Assert.False(BotPlay.ControlledFollowAttackFits(50, 70, 1));
        Assert.False(BotPlay.ControlledFollowAttackFits(0, 37, 3));
        var inFront = runtime.PlayerOf(instance, wire);
        inFront.Walk.Teleport(137, 100, 0);
        Assert.False(BotPlay.KeepControlledFollowerNear(inFront, leader, 0));
        // Ahead of the leader on its own route it steps aside and waits there, rather than standing in the corridor.
        Assert.True(inFront.ActionCount > 0);
        Assert.True(Math.Abs(inFront.Walk.Destination.Y - 100) >= 5);

        leader.Walk.Teleport(100, 100, 0);
        var idle = runtime.PlayerOf(instance, wire);
        idle.Walk.Teleport(160, 100, 0);
        action = idle.ActionCount;
        int adjustments = 0;
        for (int tick = 0; tick <= 1200; tick++)
        {
            double now = tick / 10.0; // Two minutes of simulated AI, without a real-time wait.
            Assert.False(BotPlay.KeepControlledFollowerNear(idle, leader, now),
                $"idle at {now}: follower {idle.Walk.Position(now)}, leader {leader.Walk.Position(now)}, goal {leader.Walk.Destination}");
            var before = idle.Walk.Position(now);
            BotPlay.IdleControlledFollower(idle, leader, now);
            if (idle.ActionCount != action)
            {
                adjustments++;
                // The step is drawn on a radius of 4–8 cells and then rounded to the grid, so a diagonal step can reach
                // sqrt(8²+1²) ≈ 8.06: the bound allows that rounding, not a longer walk.
                Assert.InRange(Game.Dist(before, idle.Walk.Destination), 3, 9);
                Assert.True(Game.Dist(idle.Walk.Destination, (100, 100)) <= 60);
                action = idle.ActionCount;
            }
            if (now < 14) Assert.Equal(0, adjustments);
        }
        Assert.InRange(adjustments, 1, 3);
        foreach (int gap in new[] { 0, 3, 55, 60 })
        {
            var standing = runtime.PlayerOf(instance, wire);
            standing.Walk.Teleport(100 + gap, 100, 0);
            for (int tick = 0; tick < 100; tick++)
            {
                double now = tick / 10.0;
                Assert.False(BotPlay.KeepControlledFollowerNear(standing, leader, now));
                BotPlay.IdleControlledFollower(standing, leader, now);
                Assert.Equal(0, standing.ActionCount);
            }
        }
        var delayed = runtime.PlayerOf(instance, wire);
        delayed.Walk.Teleport(180, 100, 0);
        delayed.Walk.SetSpeed(1, 0);
        Assert.True(BotPlay.KeepControlledFollowerNear(delayed, leader, 0));
        Assert.Equal((180, 100), delayed.Walk.Position(0));
        // Slow but still closing: it keeps walking instead of blinking.
        Assert.True(BotPlay.KeepControlledFollowerNear(delayed, leader, 1.3));
        Assert.True(Game.Dist(delayed.Walk.Position(1.3), (100, 100)) < 80);
        // Formation search cannot cross a disconnected wall into an apparently clear pocket.
        var isolated = new Player(new(1, "pocket", 300, 300, 100, 100,
            (x, y) => x <= 105 || x >= 115, []), 1, 1);
        Assert.True(BotPlay.ControlledFollowerPoint(isolated, leader, arrival: true).X <= 105);
        ControlledPlayerBuildChecks.VerifyReservationAttackSelection(runtime, wire);
    }

    [Fact]
    public void Idle_followers_retry_short_steps_and_leave_blocked_routes_untouched()
    {
        using var runtime = new Runtime();
        using var wire = new Wire();
        foreach (uint peer in new uint[] { 0x2000012a, 0x20000152 })
        {
            var instance = new InstanceState();
            var leader = runtime.PlayerOf(instance, wire);
            var idle = runtime.PlayerOf(instance, wire, peer: peer);
            leader.Walk.Teleport(100, 100, 0);
            idle.Walk.Teleport(160, 100, 0);
            BotPlay.IdleControlledFollower(idle, leader, 0);
            // These peers first adjust at 28 s. Their first reachable candidate rounds to (-2, 2),
            // below the three-cell movement minimum; another candidate is reachable in the same decision.
            BotPlay.IdleControlledFollower(idle, leader, 28);
            Assert.Equal(1, idle.ActionCount);
            Assert.Equal(1, idle.ChannelGeneration);
            Assert.True(idle.Walk.Moving(28));
            Assert.InRange(Game.Dist((160, 100), idle.Walk.Destination), 3, 8);
            Assert.True(Game.Dist(idle.Walk.Position(29), (160, 100)) >= 3);
            Assert.All(idle.Walk.Steps(28, 29), step =>
                Assert.True(Game.Dist(step.Cell, (100, 100)) <= BotPlay.ControlledFollowerLeash));
        }

        var blockedInstance = new InstanceState();
        var blockedLeader = runtime.PlayerOf(blockedInstance, wire);
        var blocked = runtime.PlayerOf(blockedInstance, wire, peer: 0x2000012a);
        blockedLeader.Walk.Teleport(100, 100, 0);
        blocked.Walk.Teleport(160, 100, 0);
        var barrier = new HashSet<(int X, int Y)>();
        for (int x = 152; x <= 168; x++)
            for (int y = 92; y <= 108; y++)
                if ((x, y) != (160, 100)) barrier.Add((x, y));
        blocked.Barrier = barrier;
        BotPlay.IdleControlledFollower(blocked, blockedLeader, 0);
        BotPlay.IdleControlledFollower(blocked, blockedLeader, 28);
        BotPlay.IdleControlledFollower(blocked, blockedLeader, 28.1);
        Assert.Equal(0, blocked.ActionCount);
        Assert.Equal(0, blocked.ChannelGeneration);
        Assert.Equal((160, 100), blocked.Walk.Position(29));
        Assert.False(blocked.Walk.Moving(29));
    }

    internal sealed class Runtime : IDisposable
    {
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly List<Player> players = [];
        public Runtime()
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        // peer: a fixed id for a rule that is seeded by it; otherwise the next id of the process.
        public Player PlayerOf(InstanceState instance, Wire wire, string? character = null, uint? peer = null)
        {
            AreaLayout layout = new(1, "follower", 300, 300, 100, 100, null, []);
            var player = peer is { } id
                ? new Player(layout, 1, 1) { Instance = instance, Character = { Name = character }, Id = id }
                : new Player(layout, 1, 1) { Instance = instance, Character = { Name = character } };
            player.PlayerLife = 1000; player.Connection.PlayerSent = true; player.Connection.Driven = new(); player.Connection.LockstepClient = false;
            player.Connection.Session = new("follower-test", wire.Stream, wire.Ciphers);
            players.Add(player); Program.InGame.TryAdd(player, 0);
            return player;
        }
        public void Dispose()
        {
            foreach (var player in players) { player.Closed = true; Program.InGame.TryRemove(player, out _); }
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    internal sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
