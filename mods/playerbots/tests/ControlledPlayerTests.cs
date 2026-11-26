using GameServer.Modules.PlayerBots;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

internal static class ControlledPlayerBuildChecks
{
    internal static void VerifyReservationAttackSelection(PartyFollowerTests.Runtime runtime, PartyFollowerTests.Wire wire)
    {
        var instance = new InstanceState();
        var leader = runtime.PlayerOf(instance, wire);
        var bot = runtime.PlayerOf(instance, wire, "HeraldFollower" + Guid.NewGuid().ToString("N"));
        bot.Pools = new(25, 1000, 1000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000);
        bot.Character.Passives = [.. BotPlay.BuildControlledPlayer(CharacterClass.Witch, 25).Passives!];
        bot.LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]));
        // Inspect the AI's own auto-attack scan without starting background player updates or a cast timer: a flask or a
        // walk there would persist this character's items, and these are only ever held in memory here.
        bot.Connection.PlayerSent = false;
        try
        {
            var items = Game.ItemsOf(bot);
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
            ItemSocket Socket(string name) => new(SocketColour.White, ItemCatalog.Create(Program.Items.FindGem(name)!.Value.Type));
            items.Add(PlayerInventories.Weapon1, 0, 0, wand with
            { Sockets = [Socket("Herald of Ice"), Socket("Herald of Thunder"), Socket("Fireball")] });
            var gems = Game.GemSkillEffects(bot).ToArray();
            Assert.Equal(new[] { "Herald of Ice", "Herald of Thunder", "Fireball" }, gems.Select(g => g.Name));
            Assert.All(gems, g => Assert.True(SkillHitPlan.ForSkill(Program.Skills, g.Effect, g.Level, g.Quality).CanHit));
            // The two Heralds hit reactively, but their reservation is a toggle: a bot that "cast" one would switch it
            // off. Only Fireball is left to attack with.
            Assert.Equal(gems[2].Effect, Assert.Single(BotPlay.ControlledSkills(bot)).Effect);

            // With only the reactive reservation hits equipped, automatic combat leaves their toggles alone.
            var equipped = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, equipped.Id, item => item with { Sockets = [.. item.Sockets.Take(2)] });
            Assert.Empty(BotPlay.ControlledSkills(bot));
        }
        finally
        {
            PlayerInventories.Forget(bot.Character.Name!);
            CharacterEquipmentState.Replaced(bot.Character.Name!);
        }
    }

    internal static void VerifyCampaignBuilds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"controlled-player-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var names = new List<string>();
        try
        {
            var accounts = new SqliteAccountStore(Saves.Db);
            foreach (var characterClass in Enum.GetValues<CharacterClass>())
                foreach (int level in new[] { 1, 25, 63, 75 })
                {
                    string name = $"BotBuild{(int)characterClass}L{level}";
                    names.Add(name);
                    var record = new GameServer.Core.Accounts.Character(Guid.NewGuid(), Guid.NewGuid(), name, characterClass, level,
                        "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    accounts.AddCharacter(record);
                    var build = BotPlay.BuildControlledPlayer(characterClass, level);
                    Assert.Equal(level - 1, build.Passives!.Count);
                    CharacterSaves.Write(name, build);
                    int row = Program.Skills.RowOfClass((int)characterClass);
                    var player = new Player(new(1, "controlled-build-test", 1000, 1000, 100, 100, null, []), 1, 1)
                    {
                      Character = { Name = name, Id = record.Id, League = "Standard", ClassRow = row },
                      Pools = CharacterStats.For(Program.Skills.ClassFor(row), level)
                  };
                    Assert.Equal(level, Game.PrepareCharacterForArea(player, level));
                    Game.CharacterStatSources(player);
                    Assert.Empty(CharacterEquipmentState.Inactive[name]);
                    var gems = Game.GemSkillEffects(player).ToArray();
                    var active = Assert.Single(gems);
                    var supported = Game.SupportedSkill(player, active.Id);
                    Assert.True(supported.RequirementsMet, $"{name}: {active.Name} requirements");
                    Assert.True(Game.SupportedHitFor(player, active.Id, suppress: false).MinTotal > 0, name);
                    // The link is as long as the weapon's sockets (ControlledBuilds.SocketsFor): six gems on a two-hander, a
                    // bow or a staff, three on a one-hander. Some supports do not resolve for a given active gem, so the
                    // count is a range — but a build that links two supports where six sockets stand is a quarter of its
                    // damage (2026-11-10).
                    if (level >= 25)
                    {
                        var weapon = Program.Items.ByHash(Game.ItemsOf(player).Items(PlayerInventories.Weapon1).First().Item.TypeHash)!;
                        int most = BotPlay.SocketsFor(weapon) - 1;
                        Assert.True(supported.Supports.Count >= 2 && supported.Supports.Count <= most,
                            $"{name}: {active.Name} on {weapon.Class} ({most} supports at most), supports: " +
                            $"{string.Join(", ", supported.Supports.Select(s => s.Effect.Id))}");
                    }
                    var native = Game.PlayerForCreation(player);
                    Assert.Contains(native.Skills!, s => s.Id == active.Id);
                    // The gear pass replaces every worn item. The weapon's gems have to survive it, and a weapon that lost
                    // them (an older pass stripped them) gets the class's set back: a follower with no gem has no skill at
                    // all and stood in every fight doing nothing (live 2026-11-09: "the party bots do not attack").
                    var worn = Game.ItemsOf(player);
                    var pieces = new[]
                        {
                            PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.BodyArmour,
                            PlayerInventories.Helmet, PlayerInventories.Gloves, PlayerInventories.Boots, PlayerInventories.Belt,
                        }
                        .SelectMany(row => worn.Items(row)).Select(held => Program.Items.ByHash(held.Item.TypeHash)!.Id).ToArray();
                    var gear = BotHooks.Gear;
                    BotHooks.Gear = _ => pieces;
                    try
                    {
                        BotPlay.EquipControlledGear(player, level);
                        Assert.Single(Game.GemSkillEffects(player));
                        foreach (var held in worn.Items(PlayerInventories.Weapon1).ToList())
                        {
                            worn.Remove(PlayerInventories.Weapon1, held.Id);
                            worn.Add(PlayerInventories.Weapon1, 0, 0, held.Item with { Sockets = [], Links = null });
                        }
                        Assert.Empty(Game.GemSkillEffects(player));
                        BotPlay.EquipControlledGear(player, level);
                        Assert.Single(Game.GemSkillEffects(player));
                    }
                    finally { BotHooks.Gear = gear; }
                }
        }
        finally
        {
            foreach (string name in names) { PlayerInventories.Forget(name); CharacterEquipmentState.Replaced(name); }
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
