using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.CosmeticsAndPets;
using GameServer.Modding;

public sealed class PetLootMod : IMod
{
    /// <summary>一个角色的拾取设置（/petloot 改的就是这一份），没存过的角色用服务器默认。</summary>
    public sealed record Profile(bool Enabled, bool Gold, int Range, bool CommonCurrency, Dictionary<string, int> Rules)
    {
        /// <summary>宠物跟随、拾取与返回的移动速度倍率（0.25-10，1 是原速）。</summary>
        public double MovementSpeed { get; init; } = 2;
        public bool AllCurrency { get; init; }
        public bool DivinationCards { get; init; }
        public bool MapFragments { get; init; }
    }
    /// <summary>服务器默认（settings.json 的 default）与各角色的覆盖。</summary>
    public sealed record Choices(bool Enabled, bool Gold, int Range, bool CommonCurrency,
        Dictionary<string, int> Rules, Dictionary<string, Profile> Profiles)
    {
        public double MovementSpeed { get; init; } = 2;
        public bool AllCurrency { get; init; }
        public bool DivinationCards { get; init; }
        public bool MapFragments { get; init; }
    }

    // 预设通货：和设置界面上的「常见通货」勾选对应。
    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chaos Orb", "Divine Orb", "Exalted Orb", "Mirror of Kalandra", "Orb of Annulment",
        "Orb of Alteration", "Orb of Alchemy", "Orb of Augmentation", "Orb of Chance", "Orb of Transmutation",
        "Orb of Fusing", "Chromatic Orb", "Jeweller's Orb", "Orb of Regret", "Orb of Scouring",
        "Regal Orb", "Blessed Orb", "Vaal Orb", "Gemcutter's Prism", "Glassblower's Bauble"
    };
    // 6f08140 的通货分类：勾「全部通货」时这四类都捡（a82b2f0 只认 StackableCurrency，其余三类被漏掉）。
    static readonly HashSet<string> CurrencyClasses = new(StringComparer.Ordinal)
    {
        "Currency", "StackableCurrency", "DelveSocketableCurrency", "DelveStackableSocketableCurrency",
    };
    readonly object choicesGate = new();
    readonly ConditionalWeakTable<Player, RunState> states = new();
    IModContext mod = null!;
    Choices choices = null!;

    sealed class RunState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly Dictionary<uint, double> RetryAfter = [];
        internal uint? Item, Pet;
        internal double Seconds, Started, NextLogAt;
    }

    public void Load(IModContext context)
    {
        mod = context;
        var loaded = mod.Settings<Choices>();
        // 手改 settings.json 的倍率照样钳到 0.25-10（和 /petloot speed 一致）。
        choices = loaded with
        {
            Rules = Normalize(loaded.Rules),
            MovementSpeed = Speed(loaded.MovementSpeed),
            Profiles = (loaded.Profiles ?? []).ToDictionary(p => p.Key,
                p => p.Value with { Rules = Normalize(p.Value.Rules), MovementSpeed = Speed(p.Value.MovementSpeed) }),
        };
        GameEvents.AreaTick.Add(mod.Manifest.Id, GameEvents.Always(Tick));
        // 只给这个模组管的宠物乘倍率，其它实体的移动速度原样返回。
        GameEvents.MovementSpeed.Add(mod.Manifest.Id, (player, id, speed) =>
        {
            if (!mod.Lifetime.IsActive) return speed;
            lock (player.Pets)
                return player.Pets.ContainsKey(id) ? speed * ProfileOf(player).MovementSpeed : speed;
        });
        GameEvents.Command(mod.Manifest.Id, "petloot",
            "/petloot [on|off|toggle|list|nearby|gold|common|range|speed|allow|block|remove|reset]", Command);
        mod.Log("宠物拾取已就绪：金币 / 通货 / 命运卡 / 地图碎片；/petloot 查看当前角色的设置。");
        // The declared names must be the record's names, or a setting silently stays at its default.
        mod.Log($"宠物拾取默认：{On(choices.Enabled)}；金币 {On(choices.Gold)}；常见通货 {On(choices.CommonCurrency)}；"
            + $"全部通货 {On(choices.AllCurrency)}；命运卡 {On(choices.DivinationCards)}；地图碎片 {On(choices.MapFragments)}；"
            + $"半径 {choices.Range} 格；速度 {choices.MovementSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}x");
    }

    // 账号 + 联盟 + 角色名：和 /petloot 里说的「这个角色」一致。
    static string Key(Player player) => string.Join("|", new[] { player.Character.Account ?? "", player.Character.League ?? "Standard", Program.CharacterName(player) }
        .Select(Uri.EscapeDataString));

    /// <summary>手改设置文件里的倍率同样钳到 0.25-10。</summary>
    static double Speed(double value) => double.IsFinite(value) ? Math.Clamp(value, .25, 10) : 2;

    static string On(bool value) => value ? "开" : "关";

    static Dictionary<string, int> Normalize(Dictionary<string, int>? rules)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rules ?? []) result[key] = value;
        return result;
    }

    Profile ProfileOf(Player player)
    {
        Profile profile;
        lock (choicesGate)
            profile = choices.Profiles.GetValueOrDefault(Key(player))
                ?? new(choices.Enabled, choices.Gold, choices.Range, choices.CommonCurrency, choices.Rules)
                    {
                        MovementSpeed = choices.MovementSpeed, AllCurrency = choices.AllCurrency,
                        DivinationCards = choices.DivinationCards, MapFragments = choices.MapFragments,
                    };
        // 老版本存下来的 profile 里半径字段叫 range，读成 Range 是 0；别让它被夹到最小半径。
        return profile.Range is > 0 and <= 100 ? profile : profile with { Range = 50 };
    }

    sealed record Candidate(uint Id, WorldItemObject Item, ItemCatalog.BaseType Type);

    bool Active(Player player) => mod.Lifetime.IsActive && Game.CurrentGameRules && !player.Closed
        && !player.LeavingArea && !player.Paused && player.Connection.PlayerSent && player.PlayerLife > 0 && !player.RageRetired
        && Program.ModulesFor(player).Contains("Game") && Program.ModulesFor(player).Contains("Cosmetics")
        && !Game.HardcoreProgressBlocked(player) && GameEvents.AllowsLoot(player);

    static bool Visible(Player player, uint id)
    {
        var origin = player.Instance.Ground.Origin(id);
        // 上游已移除每连接的 SeenGround 表（新版 Connection 上不再有），地面归属判断按新的 Ground.Origin 语义来。
        return origin.Shared || ReferenceEquals(origin.By, player);
    }

    static Candidate[] Nearby(Player player, Profile profile)
    {
        var pos = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
        var items = Game.ItemsFor(player);
        var nearby = new List<Candidate>();
        foreach (var (id, item) in player.Instance.Ground.Within(pos.X, pos.Y, Math.Clamp(profile.Range, 5, 100), _ => true))
            if (Visible(player, id) && items.ByHash(item.Item.TypeHash) is { } type)
                nearby.Add(new(id, item, type));
        return nearby.ToArray();
    }

    static bool Accept(Candidate drop, Profile profile)
    {
        bool gold = Game.IsGold(drop.Item);
        if (!profile.Enabled || drop.Item.PlayerDropped || drop.Type.Quest is not null
            || (gold ? !profile.Gold : drop.Type.MaxStack <= 1)) return false;
        if (!profile.Rules.TryGetValue(drop.Type.Id, out int minimum)
            && !profile.Rules.TryGetValue(drop.Type.Name, out minimum))
        {
            bool selected = gold || profile.CommonCurrency && Common.Contains(drop.Type.Name)
                || profile.AllCurrency && CurrencyClasses.Contains(drop.Type.Class)
                || drop.Type.Class switch
                {
                    "DivinationCard" => profile.DivinationCards,
                    "MapFragment" => profile.MapFragments,
                    _ => false,
                };
            minimum = selected ? 1 : 0;
        }
        return minimum > 0 && drop.Item.Item.StackSize >= minimum;
    }

    async Task Release(Player player, RunState state)
    {
        uint? pet = state.Pet;
        state.Item = state.Pet = null;
        if (pet is uint id && Active(player))
            await CosmeticsFeature.WalkPetTo(player, id, player.Walk.Position(player.Clock.Elapsed.TotalSeconds), () => Active(player));
    }

    async Task Tick(Player player, double seconds)
    {
        var state = states.GetValue(player, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            var profile = ProfileOf(player);
            if (!profile.Enabled || !Active(player)) { await Release(player, state); return; }
            state.Seconds += Math.Max(0, seconds);
            foreach (uint old in state.RetryAfter.Where(e => e.Value <= state.Seconds).Select(e => e.Key).ToArray())
                state.RetryAfter.Remove(old);
            var at = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            var inRange = Nearby(player, profile);
            Candidate? matching = null, nearest = null;
            double nearestDistance = double.PositiveInfinity;
            foreach (var candidate in inRange)
            {
                if (!Accept(candidate, profile) || state.RetryAfter.ContainsKey(candidate.Id)) continue;
                if (candidate.Id == state.Item) { matching = candidate; break; }
                double distance = Distance(at.X, at.Y, candidate.Item.GridX, candidate.Item.GridY);
                if (distance < nearestDistance) { nearestDistance = distance; nearest = candidate; }
            }
            var drop = matching ?? nearest;
            if (drop is null)
            {
                // Nothing eligible while something is on the ground: say what the filter let through and what it did not.
                if (inRange.Length > 0 && state.Seconds >= state.NextLogAt)
                {
                    state.NextLogAt = state.Seconds + 30;
                    mod.Log($"附近 {inRange.Length} 个掉落都没通过过滤（半径 {profile.Range}；金币 {On(profile.Gold)}；"
                        + $"常见通货 {On(profile.CommonCurrency)}；全部通货 {On(profile.AllCurrency)}；"
                        + $"命运卡 {On(profile.DivinationCards)}；碎片 {On(profile.MapFragments)}）："
                        + string.Join("、", inRange.Take(5).Select(c => $"{c.Type.Name} x{c.Item.Item.StackSize}")));
                }
                await Release(player, state); return;
            }
            var to = (X: drop.Item.GridX, Y: drop.Item.GridY);
            if (matching is null)
            {
                double now = player.Clock.Elapsed.TotalSeconds;
                lock (player.Pets)
                {
                    if (player.Pets.Count == 0) return;
                    state.Pet = player.Pets.MinBy(p => { var pos = p.Value.Walk.Position(now); return Distance(pos.X, pos.Y, to.X, to.Y); }).Key;
                }
                state.Item = drop.Id; state.Started = state.Seconds;
                if (!await CosmeticsFeature.WalkPetTo(player, state.Pet.Value, to, () => Active(player)))
                { await Release(player, state); return; }
            }
            Game.GroundPickup? pickup = null;
            lock (player.Pets)
            {
                if (Active(player) && state.Pet is uint id && player.Pets.TryGetValue(id, out var pet) && Visible(player, drop.Id)
                    && ReferenceEquals(player.Instance.Ground.Get(drop.Id), drop.Item))
                {
                    var current = ProfileOf(player);
                    double now = player.Clock.Elapsed.TotalSeconds;
                    var owner = player.Walk.Position(now); var collector = pet.Walk.Position(now);
                    if (Accept(drop, current) && Distance(owner.X, owner.Y, to.X, to.Y) <= Math.Clamp(current.Range, 5, 100))
                    {
                        if (Distance(collector.X, collector.Y, to.X, to.Y) <= 2)
                            pickup = Game.TakeGroundStack(player, drop.Id, drop.Item);
                        else if (state.Seconds - state.Started < 10) return;
                    }
                }
            }
            if (pickup is not null)
            {
                state.Item = null;
                await Game.SendPickup(player, drop.Id, pickup);
                return;
            }
            state.RetryAfter[drop.Id] = state.Seconds + 2;
            await Release(player, state);
        }
        finally { state.Gate.Release(); }
    }

    static double Distance(int x, int y, int toX, int toY) => Math.Sqrt(Math.Pow((double)x - toX, 2) + Math.Pow((double)y - toY, 2));

    async Task Command(Player player, string[] args)
    {
        var state = states.GetValue(player, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            if (!mod.Lifetime.IsActive) return;
            var profile = ProfileOf(player);
            string action = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            if (action is "status" or "list")
            {
                string rules = string.Join(", ", profile.Rules.OrderBy(r => r.Key).Select(r => $"{r.Key}: {(r.Value == 0 ? "屏蔽" : "最少 " + r.Value)}"));
                await player.Commands!.Reply($"宠物拾取：{On(profile.Enabled)}；金币 {On(profile.Gold)}；常见通货 {On(profile.CommonCurrency)}；"
                    + $"全部通货 {On(profile.AllCurrency)}；命运卡 {On(profile.DivinationCards)}；地图碎片 {On(profile.MapFragments)}；"
                    + $"半径 {profile.Range} 格；速度 {profile.MovementSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}x。\n"
                    + $"单项覆盖：{(rules.Length == 0 ? "无" : rules)}\n"
                    + "分类开关在启动器的模组设置里改；这里是这个角色的临时改动：/petloot on|off|toggle|gold|common|range 5-100|speed 0.25-10|allow <物品> [最少堆叠]|block|remove <物品>|nearby|reset");
                return;
            }
            if (action == "nearby")
            {
                var nearby = Nearby(player, profile).Take(12).ToArray();
                await player.Commands!.Reply(nearby.Length == 0 ? "附近没有掉落。" : string.Join("\n",
                    nearby.Select(i => $"{i.Type.Name} x{i.Item.Item.StackSize}（{i.Type.Id}）")));
                return;
            }
            if (action is "on" or "off" or "toggle")
                profile = profile with { Enabled = action == "on" || action == "toggle" && !profile.Enabled };
            else if (action is "gold" or "common" && args.Length == 2 && args[1] is "on" or "off")
                profile = action == "gold" ? profile with { Gold = args[1] == "on" } : profile with { CommonCurrency = args[1] == "on" };
            else if (action == "range" && args.Length == 2 && int.TryParse(args[1], out int range) && range is >= 5 and <= 100)
                profile = profile with { Range = range };
            else if (action == "speed")
            {
                if (args.Length != 2 || !double.TryParse(args[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double speed) || !double.IsFinite(speed) || speed is < .25 or > 10)
                { await player.Commands!.Reply("用法：/petloot speed <0.25-10>；1 是原速，2 是两倍速。"); return; }
                profile = profile with { MovementSpeed = speed };
            }
            else if (action is "allow" or "block" or "remove" && args.Length > 1)
            {
                int minimum = 1, end = args.Length;
                if (action == "allow" && args.Length > 2 && int.TryParse(args[^1], out int parsed)) { minimum = parsed; end--; }
                string name = string.Join(" ", args.Skip(1).Take(end - 1)).Trim().Trim('"');
                if (name.Length == 0 || minimum is < 1 or > 65535)
                { await player.Commands!.Reply("用法：/petloot allow <物品名或 metadata> [1-65535]。"); return; }
                var rules = new Dictionary<string, int>(profile.Rules, StringComparer.OrdinalIgnoreCase);
                if (action == "remove") rules.Remove(name);
                else rules[name] = action == "block" ? 0 : minimum;
                profile = profile with { Rules = rules };
            }
            else if (action != "reset")
            { await player.Commands!.Reply("未知选项。用 /petloot 看当前设置和命令。"); return; }

            await Release(player, state); // 暂停或改过滤后不留下旧目标
            lock (choicesGate)
            {
                var profiles = new Dictionary<string, Profile>(choices.Profiles);
                if (action == "reset") profiles.Remove(Key(player)); else profiles[Key(player)] = profile;
                var next = choices with { Profiles = profiles };
                mod.SaveSettings(next);
                choices = next;
            }
            await player.Commands!.Reply(action == "reset" ? "宠物拾取：这个角色改回服务器默认。"
                : $"已保存给角色 {Program.CharacterName(player)}：{On(profile.Enabled)}。用 /petloot list 查看。");
        }
        finally { state.Gate.Release(); }
    }
}
