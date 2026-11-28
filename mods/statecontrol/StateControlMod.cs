using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Storage;

/// <summary>
/// Save states for testing and practice: a character and the stash are copied into the save database and put back on
/// request. A state is the character's whole save and account record, or every tab of the league's stash.
///
/// A character in game is not written over while its connection lives: the connection's own last save would undo it.
/// /load_saves ends the connection instead, and the state goes in at <see cref="GameEvents.CharacterAtRest"/>, after
/// that last save and before the character's next login reads it.
/// </summary>
public sealed class StateControlMod : IMod
{
    sealed record Choices(int FillAmount, int QuestPassivePoints);
    sealed record CharacterState(GameServer.Core.Accounts.Character Record, CharacterSaves.Save Save, DateTimeOffset At);
    sealed record StashState(AccountStash.Save Header, Dictionary<int, List<CharacterSaves.SavedPlacement>> Tabs, DateTimeOffset At);

    const string CharacterKey = "mod/statecontrol/character/", StashKey = "mod/statecontrol/stash/", UndoKey = "mod/statecontrol/stash-undo/";
    const int MostCharacters = 24;

    // The characters whose state goes in when they have left the game, by character id, with when /load_saves asked.
    // CharacterAtRest does not run when the last save failed: a state older than this is dropped, not loaded at a later logout.
    readonly ConcurrentDictionary<Guid, (CharacterState State, DateTimeOffset Asked)> _pending = new();
    static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(2);
    Choices _choices = new(1000, 24);
    Action<string> _log = _ => { };

    static SqliteAccountStore Accounts => new(Saves.Db);
    static JsonSerializerOptions Json => CharacterSaves.Opts;

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;
        _choices = mod.Settings<Choices>();
        _log = mod.Log;
        GameEvents.CharacterAtRest.Add(id, conn =>
        {
            if (conn.Character.Id is not { } character || !_pending.TryRemove(character, out var pending)) return Task.CompletedTask;
            var state = pending.State;
            if (DateTimeOffset.UtcNow - pending.Asked > PendingFor) _log($"{state.Record.Name}: state of {state.At:u} not loaded: /load_saves was {pending.Asked:u}, before its last save");
            else _log(Restore(state) is { } why ? $"{state.Record.Name}: not loaded at rest: {why}" : $"{state.Record.Name}: state of {state.At:u} loaded");
            return Task.CompletedTask;
        });
        GameEvents.CommandsBuilding.Add(id, AddCommands);
    }

    ChatCommands AddCommands(ChatCommands commands, Player conn) => commands
        .Add("save_character", "/save_character（状态控制：把这个角色当前的样子存下来：等级、天赋、物品、任务）", async _ =>
        {
            if (conn.Character.Id is not { } id || Accounts.GetCharacter(id) is not { } record) { await conn.Commands!.Reply("状态控制：这个角色没有账号记录。"); return; }
            // What waits in memory (experience, moved items) is written first: the state is the save as it is now.
            Program.SaveUnsavedProgressNow("save state");
            string? why = Saves.Atomically(() =>
            {
                if (CharacterSaves.Load(record.Name) is not { } save) return "它还没有存档";
                Saves.Db.SetMeta(CharacterKey + id.ToString("N"), JsonSerializer.Serialize(new CharacterState(Accounts.GetCharacter(id) ?? record, save, DateTimeOffset.UtcNow), Json));
                return null;
            });
            _log($"{record.Name}: {(why is null ? "state saved" : "not saved: " + why)}");
            await conn.Commands!.Reply(why is null ? $"状态控制：{record.Name} 已保存。用 /load_saves 放回去。" : $"状态控制：没有保存：{why}。");
        })
        .Add("save_stash", "/save_stash（状态控制：把这个联赛仓库每一页当前的内容都存下来）", async _ =>
        {
            string key = StashOf(conn);
            int items = Saves.Atomically(() =>
            {
                var state = ReadStash(key);
                if (state is null) return -1;
                Saves.Db.SetMeta(StashKey + key, JsonSerializer.Serialize(state, Json));
                return Count(state);
            });
            _log($"stash {key}: {(items < 0 ? "nothing to save" : $"{items} item(s) saved")}");
            await conn.Commands!.Reply(items < 0 ? "状态控制：仓库还没打开过，没有可保存的内容。" : $"状态控制：仓库已保存（{items} 件物品）。用 /load_saves 放回去。");
        })
        .Add("load_saves", "/load_saves [undo]（状态控制：把存过的仓库和这个账号存过的所有角色都放回去；当前这个角色会被送回选人界面再载入。undo：回到上一次 /load_saves、/clear_stash 或 /remove_tab 之前的仓库）", async args =>
        {
            string key = StashOf(conn);
            if (args.Length > 0 && args[0].Equals("undo", StringComparison.OrdinalIgnoreCase))
            {
                int? undone = LoadStash(key, UndoKey + key, keepUndo: false);
                if (undone is not null) await ShowStashAgain(conn);
                await conn.Commands!.Reply(undone is not null
                    ? $"状态控制：仓库已回到之前的状态（{undone} 件物品）。"
                    : "状态控制：没有可撤销的内容。");
                return;
            }
            if (conn.Character.Id is not { } id || Accounts.GetCharacter(id) is not { } record) { await conn.Commands!.Reply("状态控制：这个角色没有账号记录。"); return; }
            var told = new List<string>();
            int? items = LoadStash(key, StashKey + key, keepUndo: true);
            if (items is not null) told.Add($"仓库（{items} 件物品）");
            bool leaving = false;
            foreach (var other in Accounts.CharactersOf(record.AccountId))
            {
                if (Saves.Db.GetMeta(CharacterKey + other.Id.ToString("N")) is not { } text || JsonSerializer.Deserialize<CharacterState>(text, Json) is not { } state) continue;
                if (other.Id == id) { _pending[id] = (state, DateTimeOffset.UtcNow); leaving = true; told.Add(other.Name); }
                else if (Program.InGame.Keys.Any(c => c.Character.Id == other.Id && !c.Closed)) told.Add($"{other.Name}（正在别处游戏中：未载入）");
                else told.Add(Restore(state) is { } why ? $"{other.Name}（未载入：{why}）" : other.Name);
            }
            if (told.Count == 0) { await conn.Commands!.Reply("状态控制：还没有存过任何东西（/save_character、/save_stash）。"); return; }
            _log($"{record.Name}: loading {string.Join(", ", told)}");
            if (!leaving)
            {
                if (items is not null) await ShowStashAgain(conn);
                await conn.Commands!.Reply($"状态控制：已载入 {string.Join("、", told)}。");
                return;
            }
            // This character's own state waits for its connection's last save: the connection ends now, and is not
            // waited for here, where it would wait for this very command.
            _ = Program.EndAsync(conn, "save states loaded", $"状态控制：已载入 {string.Join("、", told)}。请重新选择角色。");
        })
        .Add("clear_stash", "/clear_stash confirm（状态控制：清空这个联赛仓库的所有页；/load_saves undo 可以放回去）", async args =>
        {
            if (args.Length == 0 || !args[0].Equals("confirm", StringComparison.OrdinalIgnoreCase))
            { await conn.Commands!.Reply("状态控制：/clear_stash confirm 会清空这个联赛的所有仓库页；/load_saves undo 会把物品放回去。"); return; }
            string key = StashOf(conn);
            int removed = Saves.Atomically(() =>
            {
                if (ReadStash(key) is not { } state) return -1;
                Saves.Db.SetMeta(UndoKey + key, JsonSerializer.Serialize(state, Json));
                WriteStash(key, new(state.Header with { Tabs = [.. state.Header.Tabs.Select(t => t with { Items = [] })] },
                    state.Tabs.ToDictionary(t => t.Key, _ => new List<CharacterSaves.SavedPlacement>()), state.At));
                return Count(state);
            });
            _log($"stash {key}: {(removed < 0 ? "nothing to clear" : $"{removed} item(s) cleared")}");
            if (removed >= 0) await ShowStashAgain(conn);
            await conn.Commands!.Reply(removed < 0 ? "状态控制：仓库还没打开过。" : $"状态控制：已从仓库移除 {removed} 件物品；/load_saves undo 可以放回去。");
        })
        .Add("fill_stash", "/fill_stash [数量]（状态控制：给每个特殊仓库页塞满它自己装的东西：通货、碎片、精华、命运卡、传奇、宝石、药剂等等）", async args =>
        {
            int amount = args.Length > 0 && int.TryParse(args[0], out int wanted) && wanted > 0 ? wanted : _choices.FillAmount;
            if (Game.ItemsFor(conn).Error is { } error) { await conn.Commands!.Reply(error); return; }
            var stash = Game.PersonalStashOf(conn);
            var lines = new List<string>();
            for (int tab = 0; tab < stash.Count; tab++) if (stash.IsShownTab(tab)) lines.AddRange(await Fill(conn, stash, tab, amount));
            lines.AddRange(FillUniqueTabs(conn));
            // The tabs it added (gems, uniques) are in the client's tab list only once the list goes out again.
            await Game.SendTabListAgain(conn, InstanceStash.Personal);
            await conn.Commands!.Reply(lines.Count == 0 ? "状态控制：仓库里没有特殊仓库页。" : $"状态控制：{string.Join("；", lines)}。");
        })
        .Add("add_tab", "/add_tab <类型> [数量] [名字]（状态控制：加任意类型的仓库页：" + string.Join(", ", TabTypes.Keys) + "；/add_tab list 列出全部类型）", async args =>
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase) || !TabTypes.TryGetValue(args[0], out var type))
            {
                await conn.Commands!.Reply((args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase) ? "" : $"状态控制：没有 \"{args[0]}\" 这种仓库页类型。") +
                    $"/add_tab <类型> [数量] [名字]，可选类型：{string.Join("、", TabTypes.Keys)}。文件夹在仓库里建：某一页的设置 → 文件夹 → 新建文件夹。");
                return;
            }
            int count = 1, nameAt = 1;
            if (args.Length > 1 && int.TryParse(args[1], out int n)) { count = n; nameAt = 2; }
            if (count is < 1 or > 50) { await conn.Commands!.Reply("状态控制：/add_tab 一次加 1 到 50 页。"); return; }
            string name = args.Length > nameAt ? string.Join(' ', args[nameAt..]).Trim() : "";
            if (name.Length > 64) { await conn.Commands!.Reply("状态控制：仓库页名字最多 64 个字符。"); return; }
            var stash = Game.PersonalStashOf(conn);
            var (r, g, b) = type.Colour;
            IReadOnlyList<int> added;
            try { added = stash.AddTabs(type.Id, count, name.Length > 0 ? name : type.Name, r, g, b); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { await conn.Commands!.Reply($"状态控制：没有加成功：{e.Message}。"); return; }
            // One tab with a name of its own keeps it as typed (a premium kind can be renamed; the rest keep "name 1").
            if (count == 1 && name.Length > 0) stash.Rename(added[0], name);
            var names = added.Select(stash.NameOf).ToList();
            // A Map or Unique tab's pages are made when the stash is read: read it again.
            if (type.Id is StashRules.Map or StashRules.Unique) AccountStash.Forget(StashOf(conn));
            await Game.SendTabListAgain(conn, InstanceStash.Personal);
            await conn.Commands!.Reply($"状态控制：已添加 {string.Join("、", names)}（{type.Name}）{(StashRules.NotShown(type.Id) ? "；服务器暂时还不会显示这类物品" : "")}。");
        })
        .Add("remove_tab", "/remove_tab <名字> | all confirm（状态控制：删掉这个名字的仓库页，或者所有页，连同里面的东西；/load_saves undo 可以放回去）", async args =>
        {
            if (args.Length == 0)
            { await conn.Commands!.Reply("状态控制：/remove_tab <名字> 删掉该名字的页和里面的物品；/remove_tab all confirm 删掉所有页。/load_saves undo 可以放回去。"); return; }
            bool all = args[0].Equals("all", StringComparison.OrdinalIgnoreCase);
            if (all && !(args.Length > 1 && args[1].Equals("confirm", StringComparison.OrdinalIgnoreCase)))
            { await conn.Commands!.Reply("状态控制：/remove_tab all confirm 会删掉这个联赛仓库的所有页和里面的物品，只留一个空页。/load_saves undo 可以放回去。"); return; }
            string name = string.Join(' ', args).Trim();
            string key = StashOf(conn);
            var stash = Game.PersonalStashOf(conn);
            var tabs = Enumerable.Range(0, stash.Count).Where(t => stash.IsShownTab(t) && (all || stash.NameOf(t).Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (tabs.Count == 0) { await conn.Commands!.Reply($"状态控制：没有叫 \"{name}\" 的仓库页。"); return; }
            if (ReadStash(key) is { } before) Saves.Db.SetMeta(UndoKey + key, JsonSerializer.Serialize(before, Json));
            var names = new List<string>();
            int items = 0;
            // Folders last: a folder's tabs move out of it, and a removed tab's folder may be removed with it.
            foreach (int t in tabs.OrderBy(t => stash.TabType(t) == StashRules.Folder))
            {
                string was = stash.NameOf(t);
                if (stash.RemoveTab(t) is int n) { items += n; names.Add(was); }
            }
            // The panel is never left without a tab.
            if (stash.VisibleCount == 0 || Enumerable.Range(0, stash.Count).All(t => !stash.IsShownTab(t) || stash.TabType(t) == StashRules.Folder))
                stash.Rename(stash.AddTabs(1, 1, "仓库", 0x7c, 0x54, 0x36)[0], "仓库");
            await Game.SendTabListAgain(conn, InstanceStash.Personal);
            _log($"stash {key}: removed {names.Count} tab(s), {items} item(s)");
            await conn.Commands!.Reply($"状态控制：已删除 {(all ? $"全部 {names.Count} 页" : string.Join("、", names))}（{items} 件物品）。/load_saves undo 可以放回去。");
        })
        .Add("make_characters", "/make_characters [campaign]（状态控制：给这个账号的每个升华各造一个 100 级角色，带任务天赋点和升华点；campaign：直接站在剧情末尾，传送点全开）", async args =>
        {
            bool campaign = args.Length > 0 && args[0].Equals("campaign", StringComparison.OrdinalIgnoreCase);
            if (conn.Character.Id is not { } id || Accounts.GetCharacter(id) is not { } record) { await conn.Commands!.Reply("状态控制：这个角色没有账号记录。"); return; }
            var choices = Game.Ascendancies.Definitions.Where(d => d.Identity.Family == AscendancyFamily.Normal).ToList();
            int have = Accounts.CharactersOf(record.AccountId).Count;
            if (have + choices.Count > MostCharacters)
            { await conn.Commands!.Reply($"状态控制：每个升华各造一个角色，共 {choices.Count} 个，而一个账号最多 {MostCharacters} 个，这个账号已经有 {have} 个。请先删掉 {have + choices.Count - MostCharacters} 个。"); return; }
            var made = new List<string>();
            foreach (var choice in choices)
                if (Enum.IsDefined((CharacterClass)choice.BaseClass))
                    made.Add(MakeCharacter(record, (CharacterClass)choice.BaseClass, choice, campaign, Game.ItemsFor(conn)));
            _log($"{record.Name}: made {string.Join(", ", made)}{(campaign ? " (campaign)" : "")}");
            await conn.Commands!.Reply($"状态控制：已创建 {string.Join("、", made)}：100 级{(campaign ? "，站在剧情末尾" : "")}。它们已经在角色选择界面里。");
        });

    // ---- characters ----

    /// <summary>Puts a character's state back: its save and the account record's level and place. Null, or why not.</summary>
    static string? Restore(CharacterState state)
    {
        string? why = Saves.Atomically(() =>
        {
            var accounts = Accounts;
            if (accounts.GetCharacter(state.Record.Id) is not { } now) return "角色已经不存在了";
            if (now.AccountId != state.Record.AccountId || now.League != state.Record.League) return "它已经换到别的账号或联赛";
            if (now.HardcoreDeath is not null) return "它已经在硬核模式里死亡";
            // The name is the save's key: a renamed character keeps its new name and gets the state under it.
            CharacterSaves.Write(now.Name, state.Save);
            accounts.UpdateCharacter(now with { Class = state.Record.Class, Level = state.Record.Level, AreaId = state.Record.AreaId, LastTown = state.Record.LastTown });
            CharacterSaves.ForgetExperience(now.Name);
            PlayerInventories.Invalidate(now.Name);
            return null;
        });
        if (why is null) CharacterEquipmentState.Replaced(Accounts.GetCharacter(state.Record.Id)?.Name ?? state.Record.Name);
        return why;
    }

    string MakeCharacter(GameServer.Core.Accounts.Character owner, CharacterClass cls, AscendancyCatalog.Definition ascendancy, bool campaign, ItemCatalog items)
    {
        var accounts = Accounts;
        string called = new(ascendancy.Name.Where(char.IsLetterOrDigit).ToArray());
        string name = $"{called}_100";
        for (int n = 2; accounts.FindCharacter(name) is not null; n++) name = $"{called}_100_{n}";
        // The ascendancy chosen, with every Labyrinth's points to spend.
        var rules = new AscendancyRules(Game.Ascendancies);
        var chosen = rules.Select(ascendancy.BaseClass, null, ascendancy.Identity);
        foreach (string award in AscendancyRules.AwardKeys) chosen = rules.Award(ascendancy.BaseClass, chosen, award);
        var end = campaign ? CampaignEnd.Value : null;
        var now = DateTimeOffset.UtcNow;
        string town = end?.Town ?? "1_1_town";
        Saves.Atomically(() =>
        {
            accounts.AddCharacter(new GameServer.Core.Accounts.Character(Guid.NewGuid(), owner.AccountId, name, cls, 100, owner.League, town, now, now, town));
            // Its start kit, as its first login would make it; then what a finished campaign adds.
            PlayerInventories.SetClass(name, Program.Skills.RowOfClass((int)cls));
            PlayerInventories.For(name, items);
            var save = CharacterSaves.Load(name) ?? new CharacterSaves.Save([]);
            int fromFlags = end is null ? 0 : QuestStaticRewards.Load().PassivePoints(end.Flags);
            CharacterSaves.Write(name, save with
            {
                ExtraPassivePoints = (ushort)Math.Max(0, _choices.QuestPassivePoints - fromFlags),
                QuestFlags = end?.Flags ?? save.QuestFlags, Waypoints = end?.Waypoints ?? save.Waypoints, Visited = end?.Waypoints ?? save.Visited,
                Tutorial = end is null ? save.Tutorial : null, MainAscendancy = chosen,
            });
            PlayerInventories.Forget(name);
        });
        return name;
    }

    sealed record Campaign(List<string> Flags, List<string> Waypoints, string? Town);

    /// <summary>The end of the story as the game's own data has it: the quest flags and the town of its
    /// "EpilogueAtlasDemo" start state (CharacterStartQuestState), and every area with a waypoint.</summary>
    static readonly Lazy<Campaign?> CampaignEnd = new(() =>
    {
        if (!GameServer.Core.ClientTables.Has("CharacterStartQuestState") || !GameServer.Core.ClientTables.Has("QuestFlags") || !GameServer.Core.ClientTables.Has("WorldAreas")) return null;
        var presets = GameServer.Core.ClientTables.Table("CharacterStartQuestState").Where(r => r.Text("Id") == "EpilogueAtlasDemo").ToList();
        if (presets.Count == 0) return null;
        var preset = presets[0];
        var flagIds = GameServer.Core.ClientTables.Table("QuestFlags").Select(r => r.Text("Id") ?? "").ToList();
        var areaIds = GameServer.Core.ClientTables.Table("WorldAreas").Select(r => r.Text("Id") ?? "").ToList();
        var flags = preset.Ints("QuestFlags").Where(f => f >= 0 && f < flagIds.Count).Select(f => flagIds[f]).Where(f => f.Length > 0).Distinct().ToList();
        string? town = preset.Ints("Areas").Where(a => a >= 0 && a < areaIds.Count).Select(a => areaIds[a]).FirstOrDefault(a => a.Length > 0);
        return new(flags, [.. WaypointCatalog.Areas.Entries.Select(e => e.Id)], town);
    });

    // ---- the stash ----

    static string StashOf(Player conn) => AccountStash.Key(conn.Character.Account ?? Program.CharacterName(conn), conn.Character.League);

    static int Count(StashState state) => state.Tabs.Values.Sum(t => t.Count) + state.Header.Tabs.Sum(t => t.Items.Count);

    /// <summary>The stash as the database has it: its header and each tab's rows. Null before the stash was first opened.</summary>
    static StashState? ReadStash(string key)
    {
        if (Saves.LoadStash(key) is not { } header) return null;
        var tabs = new Dictionary<int, List<CharacterSaves.SavedPlacement>>();
        for (int tab = 0; tab < header.Tabs.Count; tab++)
            if (Saves.LoadStashTab(key, tab) is { } items) tabs[tab] = items;
        return new(header, tabs, DateTimeOffset.UtcNow);
    }

    static void WriteStash(string key, StashState state)
    {
        Saves.PutStash(key, state.Header);
        for (int tab = 0; tab < state.Header.Tabs.Count; tab++)
            Saves.PutStashTab(key, tab, state.Tabs.GetValueOrDefault(tab) ?? []);
        // The stash in memory is the old one: the next use reads the database.
        AccountStash.Forget(key);
    }

    /// <summary>Puts the stash kept under a key back; the stash it replaces is kept for /load_saves undo. Null when
    /// nothing is kept there, else the items now in the stash.</summary>
    static int? LoadStash(string key, string from, bool keepUndo) => Saves.Atomically<int?>(() =>
    {
        if (Saves.Db.GetMeta(from) is not { Length: > 0 } text || JsonSerializer.Deserialize<StashState>(text, Json) is not { } state) return null;
        var current = ReadStash(key);
        if (keepUndo && current is not null) Saves.Db.SetMeta(UndoKey + key, JsonSerializer.Serialize(current, Json));
        else if (!keepUndo) Saves.Db.SetMeta(from, "");
        // Only the tabs go back. The rest of the header is the league's as it is now: its quest flags (every character's
        // trials), gold, other mods' data, and the next item id, which never goes back, or new items would reuse ids.
        if (current is not null)
            state = state with { Header = current.Header with { Tabs = state.Header.Tabs, LastTab = state.Header.LastTab,
                NextItemId = Math.Max(current.Header.NextItemId, state.Header.NextItemId) } };
        WriteStash(key, state);
        return Count(state);
    });

    /// <summary>After the stash was replaced in the database: the client drops every tab it has loaded (S→C 0xc1) and gets
    /// the tab list again with them marked not loaded, so the panel asks for each (0xc7) as it shows it, as after a tab
    /// switch (Game.SendStashTab).</summary>
    static async Task ShowStashAgain(Player conn)
    {
        var stash = Game.PersonalStashOf(conn);
        foreach (int tab in conn.Connection.StashTabsSent.ToArray())
            await Program.Send(conn, InstanceInventories.RemoveInventory(stash.InventoryOf(tab)),
                $"0x00c1 unload stash tab {tab} inventory {stash.InventoryOf(tab)} (State Control: the stash was replaced)");
        conn.Connection.StashTabsSent.Clear();
        await Game.SendTabListAgain(conn, InstanceStash.Personal);
    }

    /// <summary>The tab kinds /add_tab makes: a StashType row (AccountStash.Types), the name new tabs get, and a colour.</summary>
    static readonly Dictionary<string, (byte Id, string Name, (byte, byte, byte) Colour)> TabTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = (0, "普通", (0x7c, 0x54, 0x36)), ["premium"] = (1, "高级", (0x7c, 0x54, 0x36)), ["quad"] = (7, "四倍", (0x7c, 0x54, 0x36)),
        ["currency"] = (StashRules.Currency, "通货", (0xaa, 0x9e, 0x82)), ["map"] = (StashRules.Map, "地图", (0x60, 0x60, 0x60)),
        ["fragment"] = (StashRules.Fragment, "碎片", (0xc8, 0xc8, 0xc8)), ["essence"] = (StashRules.Essence, "精华", (0x9a, 0x6a, 0xc8)),
        ["divination"] = (6, "命运卡", (0x0e, 0xba, 0xff)), ["unique"] = (StashRules.Unique, "传奇", (0xaf, 0x60, 0x25)),
        ["delve"] = (StashRules.Delve, "矿坑", (0x6a, 0x80, 0x9a)), ["blight"] = (StashRules.Blight, "菌潮", (0x3a, 0x8a, 0x3a)),
        ["ultimatum"] = (14, "最后通牒", (0xaa, 0x30, 0x30)), ["delirium"] = (15, "迷雾", (0xd8, 0xd8, 0xd8)),
        ["flask"] = (17, "药剂", (0x6a, 0x9a, 0xc8)), ["gem"] = (StashRules.Gem, "宝石", (0x1a, 0xa2, 0x9b)), ["breach"] = (22, "裂隙", (0x9a, 0x4a, 0xc8)),
    };

    /// <summary>The unique tabs /fill_stash keeps: the account's own, rolled as they come, and two it adds with every
    /// value at its worst and at its best.</summary>
    static readonly (string Name, int Roll)[] UniqueTabs = [("", 0), ("传奇最低变量", -1), ("传奇最高变量", 1)];

    static List<string> FillUniqueTabs(Player conn)
    {
        var told = new List<string>();
        string key = StashOf(conn);
        var stash = Game.PersonalStashOf(conn);
        static List<int> Parents(AccountStash s) => [.. Enumerable.Range(0, s.Count).Where(t => s.IsShownTab(t) && s.TabType(t) == StashRules.Unique)];
        if (Parents(stash).Count == 0) return told;
        bool added = false;
        foreach (var (name, roll) in UniqueTabs)
            if (roll != 0 && !Parents(stash).Any(t => stash.NameOf(t) == name))
            {
                stash.Rename(stash.AddTabs(StashRules.Unique, 1, name, 0xaf, 0x60, 0x25)[0], name);
                added = true;
            }
        // A unique tab's pages are made when the stash is read: read it again with the new tabs.
        if (added) { AccountStash.Forget(key); stash = Game.PersonalStashOf(conn); }
        var uniques = Game.UniquesFor(conn);
        var mods = Game.ItemsFor(conn).Mods;
        foreach (var (name, roll) in UniqueTabs)
        {
            int parent = Parents(stash).Where(t => roll == 0 ? UniqueTabs.All(u => u.Name != stash.NameOf(t)) : stash.NameOf(t) == name).DefaultIfEmpty(-1).First();
            if (parent < 0) continue;
            try
            {
                var fill = stash.FillUniques(parent, uniques, Game.ShapeOf);
                if (roll != 0)
                    foreach (int page in stash.UniqueChildren(parent))
                        foreach (var held in stash.Inventory(page).Items.ToList())
                            stash.Change(page, held.Id, item => Rolled(item, roll, uniques, mods));
                told.Add($"{stash.NameOf(parent)}：{fill.Added} 件传奇{(fill.Skipped.Count > 0 ? $"，{fill.Skipped.Count} 件服务器造不出来" : "")}");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { told.Add($"{stash.NameOf(parent)}: {e.Message}"); }
        }
        told.Add("换个区域就能看到传奇仓库页");
        return told;
    }

    /// <summary>The item at its worst (-1) or its best (1): every implicit and explicit value at that end of its range,
    /// the unique's own range where it has one, else the modifier's, and armour's base defence roll with it. The best is
    /// the highest number, or the lowest where less is better; a value that picks something stays. The same rules as
    /// gm-commands' /item_max.</summary>
    static Item Rolled(Item item, int roll, UniqueCatalog uniques, ModCatalog mods)
    {
        var own = uniques.RollsOf(item);
        return item with
        {
            DefenceBaseRoll = !item.Components.Contains("Armour") ? item.DefenceBaseRoll : roll > 0 ? Item.MaximumDefenceBaseRoll : (byte)0,
            Mods = new List<ItemMod>(item.Mods.Select(m => m.Section is not (ModSection.Implicit or ModSection.Explicit) || mods.ByHash(m.Hash) is not { } mod ? m : m with
            {
                Values = new List<int>(m.Values.Select((value, stat) =>
                {
                    if (stat >= mod.Stats.Count || PicksSomething(mod.Stats[stat].Name)) return value;
                    var (min, max) = own?.Invoke(mod, m.Section, stat) ?? mod.RangeAt(stat);
                    return (roll > 0) == LessIsBetter(mod.Stats[stat].Name) ? Math.Min(min, max) : Math.Max(min, max);
                })),
            })),
        };
    }

    /// <summary>A stat whose value says which one and not how much: a granted support gem and its socket, a Timeless
    /// Jewel's seed, conqueror and version, Thread of Hope's ring, a veiled modifier's seed.</summary>
    static bool PicksSomething(string stat) => stat.Contains("_index", StringComparison.Ordinal)
        || stat.StartsWith("local_unique_jewel_alternate_tree_", StringComparison.Ordinal)
        || stat is "local_jewel_variable_ring_radius_value" or "veiled_mod_seed";

    /// <summary>A stat where less is better: damage the character takes, what a skill costs, flask charges used,
    /// requirements, and ailments, curses and self-damage on the character. Told by the stat's name.</summary>
    static bool LessIsBetter(string stat) => LowerIsBetter.IsMatch(stat) && !OnEnemies.IsMatch(stat);

    static readonly System.Text.RegularExpressions.Regex LowerIsBetter = new(
        @"damage_taken_(from_hits_|over_time_|from_crit_)?\+|_cost_\+|charges_used_\+|requirements?_\+"
        + @"|^(local_)?self_.*(duration|effect)_\+%|(curse|chill|shocked)_effect(iveness)?_on_self_\+%|ailment_on_self_duration_\+%"
        + @"|against_self|^self_.*damage_on_.*skill_use|life_taken_per_minute|reservation_efficiency_-2%_per_1|^-%_of_your",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // "Enemies take increased damage" is damage taken too, and there more is better.
    static readonly System.Text.RegularExpressions.Regex OnEnemies = new(
        @"enem(y|ies).*damage_taken|ailment_types|chilling_areas", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The gem tabs /fill_stash keeps: every gem of a colour as it drops, and again at level 20 with 20% quality.</summary>
    static readonly (string Name, int Colour, bool Levelled, byte R, byte G, byte B)[] GemTabs =
    [
        ("红色宝石", 1, false, 0xc8, 0x3c, 0x3c), ("红色宝石 20/20", 1, true, 0xc8, 0x3c, 0x3c),
        ("绿色宝石", 2, false, 0x3c, 0xb4, 0x50), ("绿色宝石 20/20", 2, true, 0x3c, 0xb4, 0x50),
        ("蓝色宝石", 3, false, 0x3c, 0x78, 0xc8), ("蓝色宝石 20/20", 3, true, 0x3c, 0x78, 0xc8),
        ("白色宝石", 4, false, 0xc8, 0xc8, 0xc8), ("白色宝石 20/20", 4, true, 0xc8, 0xc8, 0xc8),
    ];

    /// <summary>Fills one special tab with what it holds, by the stash's own rules, and shows the change.</summary>
    static async Task<List<string>> Fill(Player conn, AccountStash stash, int tab, int amount)
    {
        var told = new List<string>();
        byte? type = stash.TabType(tab);
        string name = stash.NameOf(tab);
        if (type is null or 0 or 1 or 7) return told;   // the general tabs hold anything
        if (type == StashRules.Flask)
        {
            if (stash.FillFlasks(tab, Game.ItemsFor(conn), Game.ShapeOf, out string why) is not { } flasks) { told.Add($"{name}: {why}"); return told; }
            if (flasks.Added.Count > 0) await Game.SendTabChange(conn, stash, tab, [], flasks.Added, $"0x00bf stash tab {tab}: /fill_stash, {flasks.Added.Count} flask(s)");
            told.Add($"{name}：{flasks.Added.Count} 瓶药剂");
        }
        else if (type == StashRules.Unique) { }   // the unique tabs are filled together (FillUniqueTabs)
        else if (type == StashRules.Gem && GemTabs.Any(g => g.Name == name)) { }   // filled with the first gem tab, below
        else if (type == StashRules.Gem)
        {
            foreach (var (tabName, colour, levelled, r, g, b) in GemTabs)
            {
                int at = Enumerable.Range(0, stash.Count).FirstOrDefault(t => stash.IsShownTab(t) && stash.TabType(t) == StashRules.Gem && stash.NameOf(t) == tabName, -1);
                bool added = at < 0;
                if (added) { at = stash.AddTabs(StashRules.Gem, 1, tabName, r, g, b)[0]; stash.Rename(at, tabName); }
                var kinds = Game.ItemsFor(conn);
                var wanted = new[] { false, true }.SelectMany(support => kinds.FullShopGems(colour, support)).Select(g => ItemCatalog.Create(g.Type) with
                {
                    GemVariant = g.Variant.Index, Quality = (byte)(levelled ? 20 : 0),
                    GemExperience = levelled && kinds.Gem(g.Type) is { } info ? info.ExperienceFor(Math.Min(20, info.MaxLevel)) : 0,
                });
                if (stash.FillCells(at, wanted, i => $"{i.TypeHash:x8}:{i.GemVariant}", Game.ShapeOf, out string gemWhy) is not { } put) { told.Add($"{tabName}: {gemWhy}"); continue; }
                if (!added && put.Added.Count > 0) await Game.SendTabChange(conn, stash, at, [], put.Added, $"0x00bf stash tab {at}: /fill_stash, {put.Added.Count} gem(s)");
                told.Add($"{tabName}：{put.Added.Count} 颗宝石{(put.Skipped > 0 ? $"，{put.Skipped} 颗没地方放" : "")}{(added ? "（新建的页：换个区域才看得到）" : "")}");
            }
            return told;
        }
        else
        {
            var catalog = Game.ItemsFor(conn);
            Item? Create(uint hash) => catalog.ByHash(hash) is { Supported: true } made ? ItemCatalog.Create(made) : null;
            if (stash.FillSlots(tab, amount, Create, Game.ShapeOf, out string why) is not { } fill) { told.Add($"{name}: {why}"); return told; }
            var now = fill.Changed.Concat(fill.Added).ToList();
            // The server fills a card's slot to one complete set; a card tab holds as many as any other slot.
            if (type == StashRules.Divination)
                now = [.. stash.Inventory(tab).Items.Select(i => i.Item.StackSize == amount ? null : stash.Change(tab, i.Id, card => card with { StackSize = (ushort)Math.Min(amount, StashRules.SlotStack) })).OfType<InventoryItem>()];
            if (now.Count > 0) await Game.SendTabChange(conn, stash, tab, [.. fill.Changed.Select(i => i.Id)], now, $"0x00bf stash tab {tab}: /fill_stash {amount}, {now.Count} slot(s)");
            told.Add($"{name}：{now.Count} 种");
        }
        return told;
    }
}
