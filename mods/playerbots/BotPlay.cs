using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

using static Game;

namespace GameServer.Modules.PlayerBots;

// How a bot plays: the game's own rules, run for a player the server drives.
public static partial class BotPlay
{
    public static async Task InitializeControlledPlayer(Player player, Player leader)
    {
        RegisterVitalsAdmission(player);
        PrepareCharacterForArea(player, player.Pools.Level);
        // The gear the PlayerBots module picked for this character, put on before the peer template is built from it, and
        // the skins that gear takes, so a bot is dressed like a player of its class rather than bare.
        EquipControlledGear(player, player.Pools.Level);
        WearBotCosmetics(player);
        InitializeCharacterVitals(player);
        BodyOf(player).Prepared = true;
        var at = ControlledFollowerPoint(player, leader, arrival: true);
        player.Walk.Teleport(at.X, at.Y, AreaTime(player));
        player.PeerType = InstanceEntities.ObjectTypeHash(SkillsFor(player).ClassFor(player.Character.ClassRow).ObjectType);
        var raw = PlayerForCreation(player);
        player.ClientSkillIds = [.. raw.Skills!.Select(s => s.Id)];
        player.PeerTemplate = raw.FitSkillList().Fitted with { GridX = at.X, GridY = at.Y };
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        Program.InGame[player] = 0;
        global::HotReload.Register(player);
        await PopulateArea(player);
        if (player.Closed) return;
        PublishPlayerPose(player);
        EnsurePlayerUpdates(player);
    }

    public static async Task StepControlledPlayer(Player player, Player leader)
    {
        if (player.Closed || leader.Closed || player.LeavingArea || player.Paused || !leader.Connection.PlayerSent) return;
        EnsurePlayerUpdates(player);
        double now = AreaTime(player);
        if (player.PlayerLife == 0)
        {
            if (double.IsNaN(BodyOf(player).DeadSince)) BodyOf(player).DeadSince = now;
            if (now - BodyOf(player).DeadSince >= 3)
            {
                await Resurrect(player, inTown: false);
                BodyOf(player).DeadSince = double.NaN;
            }
            return;
        }
        bool following = KeepControlledFollowerNear(player, leader, now, ControlledExternalMovement(player, leader, now));
        if (now < BodyOf(player).NextAction) return;
        BodyOf(player).NextAction = now + .35;
        if (player.Character.Passives.Count < player.Pools.Level - 1)
        {
            var cls = (GameServer.Core.Accounts.CharacterClass)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId;
            var nodes = BuildControlledPlayer(cls, player.Pools.Level).Passives!;
            CharacterSaves.SetMainAscendancy(Program.CharacterName(player), player.Character.MainAscendancy, nodes, player.Character.MasteryEffects,
                expected: player.Character.SavedPassiveProgress);
            player.Character.Passives = [.. nodes];
            player.Character.SavedPassiveProgress = CharacterSaves.PassiveProgressSnapshot.From(CharacterSaves.Load(Program.CharacterName(player))!);
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
        if (await ExecuteControlledIntent(player, leader, now, following)) return;
        var here = player.Walk.Position(now);
        bool inTown = InTown(player);
        var plan = FollowerPlans.GetOrCreateValue(player);
        // First the thing a player reaches for: a follower that stands in the fire until it dies is what reads as an idiot.
        if (!inTown && await DrinkControlledFlask(player, now)) return;
        // Further behind than a fight may take it: it walks back first. Without this every enemy met on the way keeps it
        // fighting where it stands, the engage leash is pushed forward each time it picks one, and it never closes the gap
        // at all (live 2026-11-11: four followers stuck 110 cells behind the player, swinging at whatever came by).
        if (!inTown && Dist(here, leader.Walk.Position(now)) > ControlledEngageLeash)
        {
            plan.EngageUntil = 0;
            ControlledSmallTalkAt(player, now);
            return;
        }
        // The enemy it picks for itself: the rarest, then what is on the player's back (ControlledCombat.ControlledTarget).
        var enemy = inTown ? null : ControlledTarget(player, leader, here, now);
        if (enemy is not { } picked)
        {
            plan.EngageUntil = 0;                       // nothing to fight: the party tightens again
            if (!following) IdleControlledFollower(player, leader, now);
            ControlledSmallTalkAt(player, now);
            return;
        }
        // It has picked a fight, so the leash widens for as long as it keeps one (ControlledFollowing).
        plan.EngageUntil = now + 1.2;
        var at = picked.Monster.Walk.Position(now);
        double distance = Dist(here, at);
        if (picked.Monster.Rarity >= MonsterRarity.Rare) ControlledWarn(player, now, picked.Id, $"当心，{picked.Monster.Label}！");
        if (leader.PlayerLife > 0 && leader.PlayerMaxLife > 0 && leader.PlayerLife / (double)leader.PlayerMaxLife < .3
            && MindOf(player).Talk > .7)
            ControlledSay(player, now, "你血不多了，退一下。", gap: 120);
        var skills = ControlledSkills(player);
        if (skills.Count == 0) return;
        // A crowd inside its reach is given ground, as a player kites (ControlledMind.ControlledBackOff).
        if (ControlledBackOff(player, leader, at, distance, skills.Max(s => s.Reach), now)) return;
        // A gem that reaches it, area first when a pack stands there, and one that is both off cooldown and affordable: a
        // cooling gem hands the turn to the next instead of standing still on it.
        var reaching = ControlledSkillsFor(skills, distance, ControlledPacked(player, picked.Monster, now));
        var skill = reaching.FirstOrDefault(s => s.Mana <= player.PlayerMana && ControlledSkillReady(player, s, now));
        bool onTheMove = following || leader.Walk.Moving(now);
        double gap = Dist(here, leader.Walk.Position(now));
        double ahead = ControlledAhead(here, leader.Walk.Position(now), leader.Walk.Destination);
        if (skill is null)
        {
            if (reaching.Count == 0)
            {
                ApproachControlledTarget(player, leader, at, skills.Max(s => s.Reach), now);
                return;
            }
            // Every gem that reaches is beyond its mana: the weapon's own attack costs none and still hits, as a player's
            // fallback does. A party of bots used to stand in front of the boss with 3 to 10 mana, swinging at nothing
            // (live 2026-11-10: Malachai taking ~1,300 damage a second from five characters).
            if (reaching.Any(s => s.Mana <= player.PlayerMana)) return;   // the rest is cooling: wait a tick
            if (onTheMove && !ControlledFollowAttackFits(gap, leader.Walk.Moving(now) ? leader.Walk.Speed : 0, .6, ahead))
                return;
            HoldControlledFollowerAttack(player, now, .6, picked.Id);
            var bare = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(picked.Id)
                .U16(SkillCatalog.FirstSkillId).U16(0xffff).U16(onTheMove ? (ushort)1 : (ushort)0).ToArray();
            await OnClientMessages([(InstanceTransfer.UseSkillOnObjectId, bare)], [], player);
            return;
        }
        if (onTheMove && !ControlledFollowAttackFits(gap, leader.Walk.Moving(now) ? leader.Walk.Speed : 0, skill.Seconds, ahead))
            return;
        HoldControlledFollowerAttack(player, now, skill.Seconds, picked.Id);
        var bytes = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(picked.Id)
            .U16(skill.Id).U16(0xffff).U16(onTheMove ? (ushort)1 : (ushort)0).ToArray();
        await OnClientMessages([(InstanceTransfer.UseSkillOnObjectId, bytes)], [], player);
    }

    static double ControlledInPlaceReach(Player player, ushort skill, int effect)
    {
        if (SkillsFor(player).ActionHash(effect) == GroundSlamWave.Action)
        {
            var stats = SupportedSkill(player, skill).Stats;
            return GroundSlamWave.Length(SkillRadius(player, stats, spell: false, effect), stats.GetValueOrDefault(Stat.ActiveSkillBaseRadiusPlus));
        }
        if (SkillsFor(player).IsMelee(effect)) return WeaponFor(player).Range;
        if (SkillsFor(player).IsSelfTargeted(effect))
            return SkillRadius(player, SupportedSkill(player, skill).Stats, SkillsFor(player).IsSpell(effect), effect);
        // A spell or an arrow reaches as far as the bot fights (ControlledSight), rather than the 65 an older check used.
        return ControlledSight;
    }
}
