using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.CosmeticsAndPets;
using GameServer.Modding;

public sealed class PetLootMod : IMod
{
    /// <summary>一个账号的拾取设置（/petloot 改的就是这一份），没存过的账号用服务器默认。</summary>
    public sealed record Profile(bool Enabled, int Radius,
        bool Gold, bool Currency, bool DivinationCard, bool MapFragment, bool Gem, bool Flask, bool QuestItem,
        bool Normal, bool Magic, bool Rare, bool Unique, bool Other,
        Dictionary<string, int> Rules);

    /// <summary>服务器默认（settings.json 的 default）与各账号的覆盖。</summary>
    public sealed record Choices(bool Enabled, int Radius,
        bool Gold, bool Currency, bool DivinationCard, bool MapFragment, bool Gem, bool Flask, bool QuestItem,
        bool Normal, bool Magic, bool Rare, bool Unique, bool Other,
        Dictionary<string, int> Rules, Dictionary<string, Profile> Profiles);

    // 分类只看 ItemClasses（BaseItemTypes 的 class），和设置界面上的勾选一一对应。
    static readonly HashSet<string> CurrencyClasses = new(StringComparer.Ordinal)
    {
        "Currency", "StackableCurrency", "DelveSocketableCurrency", "DelveStackableSocketableCurrency",
    };
    static readonly HashSet<string> MapClasses = new(StringComparer.Ordinal)
    {
        "Map", "MapFragment", "LabyrinthMapItem", "MiscMapItem", "MapKey",
    };
    static readonly HashSet<string> GemClasses = new(StringComparer.Ordinal)
    {
        "Active Skill Gem", "Support Skill Gem",
    };
    // 装备（剩下的按稀有度分）:武器、护甲、首饰、箭袋、珠宝。不在这里的类别落到「其他」。
    static readonly HashSet<string> Equipment = new(StringComparer.Ordinal)
    {
        "Amulet", "Ring", "Claw", "Dagger", "Wand", "One Hand Sword", "Thrusting One Hand Sword",
        "One Hand Axe", "One Hand Mace", "Bow", "Staff", "Two Hand Sword", "Two Hand Axe", "Two Hand Mace",
        "Quiver", "Belt", "Gloves", "Boots", "Body Armour", "Helmet", "Shield", "Jewel", "AbyssJewel",
    };

    readonly object choicesGate = new();
    readonly ConditionalWeakTable<ClientState, RunState> states = new();
    IModContext mod = null!;
    Choices choices = null!;

    sealed class RunState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly Dictionary<uint, double> RetryAfter = [];
        internal uint? Item, Pet;
        internal double Seconds, Started;
    }

    public void Load(IModContext context)
    {
        mod = context;
        var loaded = mod.Settings<Choices>();
        choices = loaded with
        {
            Rules = Normalize(loaded.Rules),
            Profiles = (loaded.Profiles ?? []).ToDictionary(p => p.Key, p => p.Value with { Rules = Normalize(p.Value.Rules) }),
        };
        GameEvents.Tick.Add(mod.Manifest.Id, Tick);
        GameEvents.Command(mod.Manifest.Id, "petloot",
            "/petloot [on|off|toggle|status|nearby|radius|allow|block|remove|reset]", Command);
        mod.Log("pet pickup: gold / currency / cards / maps / gems / flasks / quest items / gear by rarity; /petloot for this account.");
    }

    // 按账号：同一账号下的角色共用一套勾选（参考界面写的「按账号生效」）。
    static string Key(ClientState conn) => string.Join("|", new[] { conn.Account ?? "", conn.League ?? "Standard" }
        .Select(Uri.EscapeDataString));

    static Dictionary<string, int> Normalize(Dictionary<string, int>? rules)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rules ?? []) result[key] = value;
        return result;
    }

    static Profile FromChoices(Choices c) => new(c.Enabled, c.Radius, c.Gold, c.Currency, c.DivinationCard,
        c.MapFragment, c.Gem, c.Flask, c.QuestItem, c.Normal, c.Magic, c.Rare, c.Unique, c.Other, c.Rules);

    Profile ProfileOf(ClientState conn)
    {
        Profile profile;
        lock (choicesGate) profile = choices.Profiles.GetValueOrDefault(Key(conn)) ?? FromChoices(choices);
        // 老版本存下来的 profile 里半径字段叫 range，读成 radius 是 0；别让它被夹成 1 格。
        return profile.Radius is >= 1 and <= 50 ? profile : profile with { Radius = 50 };
    }

    sealed record Candidate(uint Id, WorldItemObject Item, ItemCatalog.BaseType Type);

    bool Active(ClientState conn) => mod.Lifetime.IsActive && Game.CurrentGameRules && !conn.Closed
        && !conn.LeavingArea && !conn.Paused && conn.PlayerSent && conn.PlayerLife > 0 && !conn.RageRetired
        && Program.ModulesFor(conn).Contains("Game") && Program.ModulesFor(conn).Contains("Cosmetics")
        && !Game.HardcoreProgressBlocked(conn) && GameEvents.AllowsLoot(conn);

    static bool Visible(ClientState conn, uint id)
    {
        var origin = conn.Instance.Ground.Origin(id);
        return conn.SeenGround.Contains(id) && (origin.Shared || ReferenceEquals(origin.By, conn));
    }

    static Candidate[] Nearby(ClientState conn, Profile profile)
    {
        lock (conn.Monsters)
        {
            var pos = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            var items = Game.ItemsFor(conn);
            var nearby = new List<Candidate>();
            foreach (var (id, item) in conn.Instance.Ground.Within(pos.X, pos.Y, Math.Clamp(profile.Radius, 1, 50), _ => true))
                if (Visible(conn, id) && items.ByHash(item.Item.TypeHash) is { } type)
                    nearby.Add(new(id, item, type));
            return nearby.ToArray();
        }
    }

    /// <summary>掉落物是否该捡：先看单项覆盖，再按任务物品 / 金币 / ItemClasses 分类，装备按稀有度。</summary>
    static bool Accept(Candidate drop, Profile profile)
    {
        if (!profile.Enabled || drop.Item.PlayerDropped) return false;
        var type = drop.Type;
        // 单项覆盖优先：0 屏蔽，1-65535 是最少地面堆叠。
        if (profile.Rules.TryGetValue(type.Id, out int minimum) || profile.Rules.TryGetValue(type.Name, out minimum))
            return minimum > 0 && drop.Item.Item.StackSize >= minimum;
        if (type.Quest is not null) return profile.QuestItem;
        if (Game.IsGold(drop.Item)) return profile.Gold;
        string cls = type.Class;
        if (CurrencyClasses.Contains(cls)) return profile.Currency;
        if (cls == "DivinationCard") return profile.DivinationCard;
        if (MapClasses.Contains(cls)) return profile.MapFragment;
        if (GemClasses.Contains(cls)) return profile.Gem;
        if (cls.Contains("Flask", StringComparison.Ordinal)) return profile.Flask;
        if (Equipment.Contains(cls)) return drop.Item.Item.Rarity switch
        {
            ItemRarity.Unique => profile.Unique,
            ItemRarity.Rare => profile.Rare,
            ItemRarity.Magic => profile.Magic,
            _ => profile.Normal,
        };
        return profile.Other;
    }

    async Task Release(ClientState conn, RunState state)
    {
        uint? pet = state.Pet;
        state.Item = state.Pet = null;
        if (pet is not uint id) return;
        // 松开占用：跟随恢复。这一步必须无条件做，否则「暂停拾取」之后宠物会永远不跟随。
        CosmeticsFeature.SetPetBusy(conn, id, false);
        if (Active(conn))
            await CosmeticsFeature.WalkPetTo(conn, id, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), () => Active(conn));
    }

    async Task Tick(ClientState conn, double seconds)
    {
        var state = states.GetValue(conn, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            var profile = ProfileOf(conn);
            if (!profile.Enabled || !Active(conn)) { await Release(conn, state); return; }
            state.Seconds += Math.Max(0, seconds);
            foreach (uint old in state.RetryAfter.Where(e => e.Value <= state.Seconds).Select(e => e.Key).ToArray())
                state.RetryAfter.Remove(old);
            var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            var candidates = Nearby(conn, profile).Where(i => Accept(i, profile) && !state.RetryAfter.ContainsKey(i.Id));
            var drop = state.Item is uint target ? candidates.FirstOrDefault(i => i.Id == target)
                : candidates.MinBy(i => Distance(at.X, at.Y, i.Item.GridX, i.Item.GridY));
            if (drop is null) { await Release(conn, state); return; }
            var to = (X: drop.Item.GridX, Y: drop.Item.GridY);
            if (state.Item is null)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                uint? previous = state.Pet;
                lock (conn.Pets)
                {
                    if (conn.Pets.Count == 0) return;
                    state.Pet = conn.Pets.MinBy(p => { var pos = p.Value.Walk.Position(now); return Distance(pos.X, pos.Y, to.X, to.Y); }).Key;
                }
                // 换了一只宠物：松开上一只的占用，否则它会永远停在不跟随的状态。
                if (previous is uint old && old != state.Pet) CosmeticsFeature.SetPetBusy(conn, old, false);
                state.Item = drop.Id; state.Started = state.Seconds;
                // 占住这只宠物：这一批通货捡完之前它不跟随，免得刚走到物品旁就被跟随拉回去。
                CosmeticsFeature.SetPetBusy(conn, state.Pet.Value, true);
                if (!await CosmeticsFeature.WalkPetTo(conn, state.Pet.Value, to, () => Active(conn)))
                { await Release(conn, state); return; }
            }
            Game.GroundPickup? pickup = null;
            lock (conn.Monsters)
            lock (conn.Pets)
            {
                if (Active(conn) && state.Pet is uint id && conn.Pets.TryGetValue(id, out var pet) && Visible(conn, drop.Id)
                    && ReferenceEquals(conn.Instance.Ground.Get(drop.Id), drop.Item))
                {
                    var current = ProfileOf(conn);
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    var owner = conn.Walk.Position(now); var collector = pet.Walk.Position(now);
                    if (Accept(drop, current) && Distance(owner.X, owner.Y, to.X, to.Y) <= Math.Clamp(current.Radius, 1, 50))
                    {
                        if (Distance(collector.X, collector.Y, to.X, to.Y) <= 2)
                            pickup = Game.TakeGroundStack(conn, drop.Id, drop.Item);
                        else if (state.Seconds - state.Started < 10) return;
                    }
                }
            }
            if (pickup is not null)
            {
                await Game.SendPickup(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, drop.Id, pickup);
                // 捡到了：这一件结束，但宠物继续被占用（不清 busy），下一个 tick 接着捡下一件。
                state.Item = null;
            }
            else
            {
                // 这件拿不到（超时或被挡）：放弃它换下一件，占用同样保持。
                state.RetryAfter[drop.Id] = state.Seconds + 2;
                state.Item = null;
            }
            // 这里以前每 tick 都 Release —— 那会顺手把「占用」解掉，宠物下一个 tick 就被跟随逻辑拉走，
            // 等于这批通货没捡完就跟着玩家跑了（live 2026-11-13）。释放只发生在没有可捡物品时（drop is null）。
        }
        finally { state.Gate.Release(); }
    }

    static double Distance(int x, int y, int toX, int toY) => Math.Sqrt(Math.Pow((double)x - toX, 2) + Math.Pow((double)y - toY, 2));

    string Flags(Profile p) => string.Join(" ", new (string Name, bool On)[]
    {
        ("金币", p.Gold), ("通货", p.Currency), ("命运卡", p.DivinationCard), ("地图/碎片", p.MapFragment),
        ("技能石", p.Gem), ("药剂", p.Flask), ("任务物品", p.QuestItem), ("普通装备", p.Normal),
        ("魔法装备", p.Magic), ("稀有装备", p.Rare), ("传奇装备", p.Unique), ("其他", p.Other),
    }.Where(f => f.On).Select(f => f.Name));

    async Task Command(ClientState conn, string[] args)
    {
        var state = states.GetValue(conn, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            if (!mod.Lifetime.IsActive) return;
            var profile = ProfileOf(conn);
            string action = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            if (action is "status" or "list")
            {
                string rules = string.Join(", ", profile.Rules.OrderBy(r => r.Key).Select(r => $"{r.Key}: {(r.Value == 0 ? "屏蔽" : "最少 " + r.Value)}"));
                await conn.Commands!.Reply($"宠物拾取：{(profile.Enabled ? "开" : "关")}；半径 {profile.Radius} 格；勾选 {Flags(profile)}。\n"
                    + $"单项覆盖：{(rules.Length == 0 ? "无" : rules)}\n"
                    + "分类勾选在启动器的模组设置里改；这里是这个账号的临时改动：/petloot on|off|radius 1-50|allow <物品> [最少堆叠]|block|remove <物品>|nearby|reset");
                return;
            }
            if (action == "nearby")
            {
                var nearby = Nearby(conn, profile).Take(12).ToArray();
                await conn.Commands!.Reply(nearby.Length == 0 ? "附近没有掉落。" : string.Join("\n",
                    nearby.Select(i => $"{i.Type.Name} x{i.Item.Item.StackSize}（{i.Type.Class}，{i.Item.Item.Rarity}）: {i.Type.Id}")));
                return;
            }
            if (action is "on" or "off" or "toggle")
                profile = profile with { Enabled = action == "on" || action == "toggle" && !profile.Enabled };
            else if (action == "radius" && args.Length == 2 && int.TryParse(args[1], out int radius) && radius is >= 1 and <= 50)
                profile = profile with { Radius = radius };
            else if (action is "allow" or "block" or "remove" && args.Length > 1)
            {
                int minimum = 1, end = args.Length;
                if (action == "allow" && args.Length > 2 && int.TryParse(args[^1], out int parsed)) { minimum = parsed; end--; }
                string name = string.Join(" ", args.Skip(1).Take(end - 1)).Trim().Trim('"');
                if (name.Length == 0 || minimum is < 1 or > 65535)
                { await conn.Commands!.Reply("用法：/petloot allow <物品名或 metadata> [1-65535]。"); return; }
                var rules = new Dictionary<string, int>(profile.Rules, StringComparer.OrdinalIgnoreCase);
                if (action == "remove") rules.Remove(name);
                else rules[name] = action == "block" ? 0 : minimum;
                profile = profile with { Rules = rules };
            }
            else if (action != "reset")
            { await conn.Commands!.Reply("未知选项。用 /petloot 看当前设置和命令。"); return; }

            await Release(conn, state); // 暂停或改过滤后不留下旧目标
            lock (choicesGate)
            {
                var profiles = new Dictionary<string, Profile>(choices.Profiles);
                if (action == "reset") profiles.Remove(Key(conn)); else profiles[Key(conn)] = profile;
                var next = choices with { Profiles = profiles };
                mod.SaveSettings(next);
                choices = next;
            }
            await conn.Commands!.Reply(action == "reset" ? "宠物拾取：这个账号改回服务器默认。"
                : $"已保存给账号 {Program.CharacterName(conn)}：{(profile.Enabled ? "开" : "关")}。用 /petloot 查看。");
        }
        finally { state.Gate.Release(); }
    }
}
