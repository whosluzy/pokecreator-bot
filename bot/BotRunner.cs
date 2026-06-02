using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using PokecreatorApi.Models;
using PokecreatorApi.Services;

namespace PokecreatorBot;

/// <summary>
/// Hosts the Discord bot. Usable from the console host or the WinForms setup UI.
/// Raise <see cref="Log"/> for status lines; call <see cref="StartAsync"/> / <see cref="StopAsync"/>.
/// </summary>
public sealed class BotRunner
{
    public event Action<string>? Log;
    public bool IsRunning { get; private set; }

    private readonly PkHexService _svc = new();
    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, List<ItemInfo>> _itemCache = new();
    private DiscordSocketClient? _client;
    private string? _guildId;
    private ulong _channelId; // 0 = respond everywhere

    private static readonly string[] StatNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
    private static readonly string[] TeraTypes =
    [
        "Normal","Fighting","Flying","Poison","Ground","Rock","Bug","Ghost","Steel",
        "Fire","Water","Grass","Electric","Psychic","Ice","Dragon","Dark","Fairy","Stellar",
    ];
    private static readonly string[] Languages = ["English","Japanese","French","Italian","German","Spanish","Korean"];

    private static readonly (string, string)[] Sections =
    [
        ("pokemon", "🔹 Species / Nature / Level / Form"),
        ("battle", "⚔️ Ability / Gender / Tera"),
        ("items", "🎒 Ball / Held Item / Language"),
        ("statsmoves", "📊 EVs / IVs / Moves"),
        ("cosmetic", "✨ Shiny / Alpha / Size / Extras"),
        ("trainer", "🪪 Trainer (OT / TID / SID)"),
    ];

    public async Task StartAsync(string token, string? guildId = null, string? channelId = null)
    {
        if (IsRunning) return;
        _guildId = guildId;
        _channelId = ulong.TryParse(channelId, out var ch) ? ch : 0;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            LogLevel = LogSeverity.Info,
        });
        _client.Log += m => { Log?.Invoke($"[{m.Severity}] {m.Source}: {m.Message} {m.Exception}"); return Task.CompletedTask; };
        _client.Ready += OnReady;
        _client.Connected += () => { IsRunning = true; Log?.Invoke("Connected."); return Task.CompletedTask; };
        _client.Disconnected += _ => { IsRunning = false; Log?.Invoke("Disconnected."); return Task.CompletedTask; };
        _client.SelectMenuExecuted += OnSelect;
        _client.ButtonExecuted += OnButton;
        _client.ModalSubmitted += OnModal;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
        Log?.Invoke("Login OK — starting up…");
    }

    public async Task StopAsync()
    {
        if (_client is null) return;
        Log?.Invoke("Stopping…");
        await _client.StopAsync();
        await _client.LogoutAsync();
        _client.Dispose();
        _client = null;
        IsRunning = false;
        Log?.Invoke("Stopped.");
    }

    private async Task OnReady()
    {
        // No slash commands — interaction is via the persistent channel panel.
        // Clean up any previously-registered /create so it disappears.
        try
        {
            await _client!.BulkOverwriteGlobalApplicationCommandsAsync([]);
            if (ulong.TryParse(_guildId, out var gid) && _client.GetGuild(gid) is { } guild)
                await guild.BulkOverwriteApplicationCommandAsync([]);
        }
        catch (Exception ex) { Log?.Invoke("Command cleanup note: " + ex.Message); }

        Log?.Invoke("Ready. Use \"Post Creator Panel\" to place the button in your channel.");
    }

    /// <summary>Posts the permanent creator panel (embed + button) into the configured channel.</summary>
    public async Task PostPanelAsync()
    {
        if (_client is null || !IsRunning) { Log?.Invoke("Start the bot before posting the panel."); return; }
        if (_channelId == 0) { Log?.Invoke("Set a Channel ID first, then post the panel."); return; }

        var channel = _client.GetChannel(_channelId) as IMessageChannel
                      ?? await _client.Rest.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel is null)
        {
            Log?.Invoke($"Could not find channel {_channelId}. Check the Channel ID and that the bot can see it.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("✨ Pokémon Creator")
            .WithDescription("Click **Create Pokémon** below to build a fully legal Pokémon.\n" +
                             "Your editor is **private — only you can see it**, and you'll get the `.trade` text when you're done.")
            .WithColor(new Color(0x7c, 0x3a, 0xed))
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("🛠️ Create Pokémon", "open_creator", ButtonStyle.Primary)
            .Build();

        await channel.SendMessageAsync(embed: embed, components: comp);
        Log?.Invoke($"Posted creator panel to channel {_channelId}.");
    }

    // ───────────────── interaction handlers ─────────────────

    private async Task OnSelect(SocketMessageComponent c)
    {
        if (!_sessions.TryGetValue(c.User.Id, out var s)) { await Stale(c); return; }
        var v = c.Data.Values.FirstOrDefault() ?? "";
        switch (c.Data.CustomId)
        {
            case "section": s.Section = v; break;
            case "game": ApplyGame(s, v); break;
            case "form": s.Form = int.Parse(v); ApplyMeta(s); RefreshLists(s); break;
            case "nature": s.Nature = int.Parse(v); break;
            case "ability": s.Ability = int.Parse(v); break;
            case "gender": s.Gender = int.Parse(v); break;
            case "ball": s.Ball = int.Parse(v); break;
            case "tera": s.TeraType = int.Parse(v); break;
            case "language": s.Language = v; break;
        }
        await c.UpdateAsync(m => { m.Embed = BuildEmbed(s); m.Components = BuildComponents(s); });
    }

    private async Task OnButton(SocketMessageComponent c)
    {
        // The panel button opens a fresh private editor for whoever clicked.
        if (c.Data.CustomId == "open_creator")
        {
            if (_channelId != 0 && c.ChannelId != _channelId)
            {
                await c.RespondAsync($"❌ This panel only works in <#{_channelId}>.", ephemeral: true);
                return;
            }
            var fresh = new Session();
            ApplyGame(fresh, "SV");
            _sessions[c.User.Id] = fresh;
            await c.RespondAsync(embed: BuildEmbed(fresh), components: BuildComponents(fresh), ephemeral: true);
            return;
        }

        if (!_sessions.TryGetValue(c.User.Id, out var s)) { await Stale(c); return; }
        switch (c.Data.CustomId)
        {
            case "set_pkm": await c.RespondWithModalAsync(PkmModal(s)); return;
            case "edit_stats": await c.RespondWithModalAsync(StatsModal(s)); return;
            case "edit_moves": await c.RespondWithModalAsync(MovesModal(s)); return;
            case "edit_item": await c.RespondWithModalAsync(ItemModal(s)); return;
            case "edit_extras": await c.RespondWithModalAsync(ExtrasModal(s)); return;
            case "edit_trainer": await c.RespondWithModalAsync(TrainerModal(s)); return;
            case "shiny": if (s.Meta?.CanBeShiny == true) s.Shiny = !s.Shiny; break;
            case "alpha": if (s.Meta?.HasAlpha == true) s.Alpha = !s.Alpha; break;
            case "customot": s.UseCustomOT = !s.UseCustomOT; break;
            case "generate": await c.RespondAsync(BuildTradeText(s), ephemeral: true); return;
        }
        await c.UpdateAsync(m => { m.Embed = BuildEmbed(s); m.Components = BuildComponents(s); });
    }

    private async Task OnModal(SocketModal m)
    {
        if (!_sessions.TryGetValue(m.User.Id, out var s)) { await Stale(m); return; }
        var fields = m.Data.Components.ToList();
        string Val(string id) => fields.FirstOrDefault(x => x.CustomId == id)?.Value?.Trim() ?? "";

        switch (m.Data.CustomId)
        {
            case "pkm_modal":
            {
                var match = ResolveSpecies(s, Val("name"));
                if (match is null) { await m.RespondAsync($"❌ No Pokémon matching \"{Val("name")}\" in {GameName(s.Game)}.", ephemeral: true); return; }
                s.Species = match.Id; s.SpeciesName = match.Name; s.Form = match.Form;
                ApplyMeta(s); RefreshLists(s);
                if (int.TryParse(Val("level"), out var lvl)) s.Level = Math.Clamp(lvl, s.Meta?.MinLevel ?? 1, 100);
                break;
            }
            case "stats_modal":
                s.EVs = ParseStats(Val("evs"), s.StatMax(), s.EVs);
                s.IVs = ParseStats(Val("ivs"), 31, s.IVs);
                ClampEvTotal(s);
                break;
            case "moves_modal":
                for (int i = 0; i < 4; i++) s.Moves[i] = ResolveMove(s, Val($"m{i}"));
                break;
            case "item_modal":
                s.HeldItem = ResolveItem(s, Val("item"));
                break;
            case "extras_modal":
                s.Nickname = Val("nick");
                if (int.TryParse(Val("friend"), out var fr)) s.Friendship = Math.Clamp(fr, 0, 255);
                if (int.TryParse(Val("scale"), out var sc)) s.Scale = Math.Clamp(sc, 0, 255);
                if (DateOnly.TryParse(Val("met"), out var d)) s.MetDate = d.ToString("yyyy-MM-dd");
                break;
            case "trainer_modal":
                s.OT = Val("ot"); s.UseCustomOT = true;
                if (int.TryParse(Val("tid"), out var tid)) s.TID = Math.Clamp(tid, 0, 65535);
                if (int.TryParse(Val("sid"), out var sid)) s.SID = Math.Clamp(sid, 0, 65535);
                break;
        }
        await m.UpdateAsync(x => { x.Embed = BuildEmbed(s); x.Components = BuildComponents(s); });
    }

    // ───────────────── state ─────────────────

    private void ApplyGame(Session s, string game)
    {
        s.Game = game;
        s.SpeciesList = _svc.GetAllSpecies(game);
        var first = s.SpeciesList.FirstOrDefault();
        s.Species = first?.Id ?? 0; s.SpeciesName = first?.Name ?? ""; s.Form = first?.Form ?? 0;
        ApplyMeta(s); RefreshLists(s);
    }

    private void ApplyMeta(Session s)
    {
        if (s.Species == 0) { s.Meta = null; return; }
        s.Meta = _svc.GetMeta(s.Game, s.Species, s.Form);
        s.Level = Math.Clamp(s.Level, s.Meta.MinLevel > 0 ? s.Meta.MinLevel : 1, 100);
        if (!s.Meta.CanBeShiny) s.Shiny = false;
        if (!s.Meta.HasAlpha) s.Alpha = false;
        if (s.Meta.ValidGenders.Count > 0 && s.Meta.ValidGenders.All(g => g.Value != s.Gender))
            s.Gender = s.Meta.ValidGenders[0].Value;
    }

    private void RefreshLists(Session s)
    {
        if (s.Species == 0) return;
        s.AbilityList = _svc.GetAbilities(s.Game, s.Species, s.Form);
        if (s.AbilityList.Count > 0 && s.AbilityList.All(a => a.Id != s.Ability)) s.Ability = s.AbilityList[0].Id;
        s.BallList = _svc.GetBalls(s.Game, s.Species, s.Form);
        if (s.BallList.Count > 0 && s.BallList.All(b => b.Id != s.Ball)) s.Ball = s.BallList[0].Id;
        s.MoveList = _svc.GetMoves(s.Game, s.Species, s.Form);
    }

    private List<ItemInfo> Items(string game) => _itemCache.GetOrAdd(game, g => _svc.GetItems(g));

    private static SpeciesInfo? ResolveSpecies(Session s, string name)
    {
        var q = name.ToLowerInvariant();
        return s.SpeciesList.FirstOrDefault(x => x.Name.ToLowerInvariant() == q)
            ?? s.SpeciesList.FirstOrDefault(x => x.Name.ToLowerInvariant().StartsWith(q))
            ?? s.SpeciesList.FirstOrDefault(x => x.Name.ToLowerInvariant().Contains(q));
    }

    private static int ResolveMove(Session s, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var q = name.ToLowerInvariant();
        var mv = s.MoveList.FirstOrDefault(x => x.Name.ToLowerInvariant() == q)
              ?? s.MoveList.FirstOrDefault(x => x.Name.ToLowerInvariant().StartsWith(q))
              ?? s.MoveList.FirstOrDefault(x => x.Name.ToLowerInvariant().Contains(q));
        return mv?.Id ?? 0;
    }

    private int ResolveItem(Session s, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;
        var q = name.ToLowerInvariant();
        var items = Items(s.Game);
        var it = items.FirstOrDefault(x => x.Name.ToLowerInvariant() == q)
              ?? items.FirstOrDefault(x => x.Name.ToLowerInvariant().StartsWith(q))
              ?? items.FirstOrDefault(x => x.Name.ToLowerInvariant().Contains(q));
        return it?.Id ?? 0;
    }

    private static int[] ParseStats(string raw, int max, int[] fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var parts = raw.Split(['/', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outv = (int[])fallback.Clone();
        for (int i = 0; i < 6 && i < parts.Length; i++)
            if (int.TryParse(parts[i], out var n)) outv[i] = Math.Clamp(n, 0, max);
        return outv;
    }

    private static void ClampEvTotal(Session s)
    {
        if (s.Meta?.StatSystem != "EV") return;
        for (int i = 5; i >= 0 && s.EVs.Sum() > 510; i--)
        {
            int over = s.EVs.Sum() - 510;
            s.EVs[i] = Math.Max(0, s.EVs[i] - over);
        }
    }

    private PokemonConfig BuildConfig(Session s) => new()
    {
        Game = s.Game, Species = s.Species, Form = s.Form, Level = s.Level,
        IsShiny = s.Shiny, IsAlpha = s.Alpha, Gender = s.Gender, Nature = s.Nature,
        Ability = s.Ability, HeldItem = s.HeldItem, Ball = s.Ball,
        Moves = (int[])s.Moves.Clone(), EVs = (int[])s.EVs.Clone(), IVs = (int[])s.IVs.Clone(),
        Friendship = s.Friendship, Scale = s.Scale, MetDate = s.MetDate,
        UseCustomOT = s.UseCustomOT, OT = s.OT, TID = s.TID, SID = s.SID,
        Language = s.Language, Nickname = s.Nickname, TeraType = s.TeraType,
    };

    private string BuildTradeText(Session s)
    {
        if (s.Species == 0) return "Pick a Pokémon first (**Set Pokémon**).";
        return $"```\n{_svc.ToShowdown(BuildConfig(s))}\n```";
    }

    // ───────────────── UI ─────────────────

    private Embed BuildEmbed(Session s)
    {
        var formTxt = s.Form > 0 ? $" ({FormNameOf(s)})" : "";
        var statLabel = s.Meta?.StatSystem ?? "EV";
        var moves = string.Join(", ", s.Moves.Where(m => m > 0).Select(id => MoveName(s, id)));
        var eb = new EmbedBuilder()
            .WithTitle("🛠️ PokeCreator — full editor")
            .WithColor(new Color(0x7c, 0x3a, 0xed))
            .AddField("Game", GameName(s.Game), true)
            .AddField("Pokémon", s.Species == 0 ? "—" : $"{s.SpeciesName}{formTxt}", true)
            .AddField("Level", s.Level.ToString(), true)
            .AddField("Nature", NatureName(s.Nature), true)
            .AddField("Ability", AbilityName(s, s.Ability), true)
            .AddField("Gender", GenderName(s.Gender), true)
            .AddField("Shiny", s.Shiny ? "✨ Yes" : "No", true)
            .AddField("Alpha", s.Meta?.HasAlpha == true ? (s.Alpha ? "α Yes" : "No") : "N/A", true)
            .AddField("Ball", BallName(s, s.Ball), true)
            .AddField("Held Item", s.HeldItem == 0 ? "None" : ItemName(s, s.HeldItem), true)
            .AddField(statLabel + "s", FormatStats(s.EVs), true)
            .AddField("IVs", FormatStats(s.IVs), true)
            .AddField("Moves", string.IsNullOrEmpty(moves) ? "—" : moves, false)
            .AddField("Friendship", s.Friendship.ToString(), true)
            .AddField("Met Date", s.MetDate, true)
            .AddField("Trainer", s.UseCustomOT ? $"{s.OT} ({s.TID}/{s.SID})" : "AutoOT", true);

        if (s.Meta?.HasTeraType == true) eb.AddField("Tera", TeraTypes.ElementAtOrDefault(s.TeraType) ?? "—", true);
        if (s.Meta?.HasScale == true) eb.AddField("Size", $"{s.Scale}", true);
        eb.WithFooter($"Editing: {SectionLabel(s.Section)} — use the menu to switch sections");
        return eb.Build();
    }

    private MessageComponent BuildComponents(Session s)
    {
        var b = new ComponentBuilder();
        var sec = new SelectMenuBuilder().WithCustomId("section").WithPlaceholder("Edit section…");
        foreach (var (id, label) in Sections) sec.AddOption(label, id, isDefault: id == s.Section);
        b.WithSelectMenu(sec, 0);

        int r = 1;
        switch (s.Section)
        {
            case "pokemon":
                b.WithSelectMenu(GameMenu(s), r++);
                if (Forms(s).Count > 1) b.WithSelectMenu(FormMenu(s), r++);
                b.WithSelectMenu(NatureMenu(s), r++);
                b.WithButton("Set Pokémon / Level", "set_pkm", ButtonStyle.Primary, row: 4);
                break;
            case "battle":
                b.WithSelectMenu(AbilityMenu(s), r++);
                b.WithSelectMenu(GenderMenu(s), r++);
                if (s.Meta?.HasTeraType == true) b.WithSelectMenu(TeraMenu(s), r++);
                break;
            case "items":
                b.WithSelectMenu(BallMenu(s), r++);
                b.WithSelectMenu(LanguageMenu(s), r++);
                b.WithButton("Set Held Item", "edit_item", ButtonStyle.Primary, row: 4);
                break;
            case "statsmoves":
                b.WithButton("Edit EVs / IVs", "edit_stats", ButtonStyle.Primary, row: 4);
                b.WithButton("Edit Moves", "edit_moves", ButtonStyle.Primary, row: 4);
                break;
            case "cosmetic":
                b.WithButton(s.Shiny ? "✨ Shiny: ON" : "Shiny: OFF", "shiny",
                    s.Shiny ? ButtonStyle.Success : ButtonStyle.Secondary, disabled: s.Meta is { CanBeShiny: false }, row: 4);
                b.WithButton(s.Alpha ? "α Alpha: ON" : "Alpha: OFF", "alpha",
                    s.Alpha ? ButtonStyle.Success : ButtonStyle.Secondary, disabled: s.Meta is not { HasAlpha: true }, row: 4);
                b.WithButton("Size / Friendship / Date / Nickname", "edit_extras", ButtonStyle.Primary, row: 4);
                break;
            case "trainer":
                b.WithButton(s.UseCustomOT ? "Custom OT: ON" : "AutoOT", "customot",
                    s.UseCustomOT ? ButtonStyle.Success : ButtonStyle.Secondary, row: 4);
                b.WithButton("Edit OT / TID / SID", "edit_trainer", ButtonStyle.Primary, row: 4);
                break;
        }
        _ = r;
        b.WithButton("⚡ Generate .trade", "generate", ButtonStyle.Success, disabled: s.Species == 0, row: 4);
        return b.Build();
    }

    private SelectMenuBuilder GameMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("game").WithPlaceholder("Game");
        foreach (var g in _svc.GetGames()) m.AddOption(g.Name, g.Id, isDefault: g.Id == s.Game);
        return m;
    }
    private SelectMenuBuilder NatureMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("nature").WithPlaceholder("Nature");
        foreach (var n in _svc.GetNatures())
        {
            var mod = n.RaisedStat != "—" ? $"+{n.RaisedStat} -{n.LoweredStat}" : "Neutral";
            m.AddOption($"{n.Name} ({mod})", n.Id.ToString(), isDefault: n.Id == s.Nature);
        }
        return m;
    }
    private static List<SpeciesInfo> Forms(Session s) => s.SpeciesList.Where(x => x.Id == s.Species).ToList();
    private static SelectMenuBuilder FormMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("form").WithPlaceholder("Form");
        foreach (var f in Forms(s).Take(25)) m.AddOption(f.FormName ?? "Default", f.Form.ToString(), isDefault: f.Form == s.Form);
        return m;
    }
    private static SelectMenuBuilder AbilityMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("ability").WithPlaceholder("Ability");
        if (s.AbilityList.Count == 0) m.AddOption("—", "0", isDefault: true);
        foreach (var a in s.AbilityList) m.AddOption($"{a.Name}{(a.IsHidden ? " (HA)" : "")}", a.Id.ToString(), isDefault: a.Id == s.Ability);
        return m;
    }
    private static SelectMenuBuilder GenderMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("gender").WithPlaceholder("Gender");
        var gs = s.Meta?.ValidGenders ?? [new GenderOption(0, "Male"), new GenderOption(1, "Female")];
        foreach (var g in gs) m.AddOption(g.Name, g.Value.ToString(), isDefault: g.Value == s.Gender);
        return m;
    }
    private static SelectMenuBuilder BallMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("ball").WithPlaceholder("Ball");
        if (s.BallList.Count == 0) m.AddOption("Poké Ball", "4", isDefault: true);
        foreach (var ball in s.BallList.Take(25)) m.AddOption(ball.Name, ball.Id.ToString(), isDefault: ball.Id == s.Ball);
        return m;
    }
    private static SelectMenuBuilder TeraMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("tera").WithPlaceholder("Tera Type");
        for (int i = 0; i < TeraTypes.Length; i++) m.AddOption(TeraTypes[i], i.ToString(), isDefault: i == s.TeraType);
        return m;
    }
    private static SelectMenuBuilder LanguageMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("language").WithPlaceholder("Language");
        foreach (var l in Languages) m.AddOption(l, l, isDefault: l == s.Language);
        return m;
    }

    private static Modal PkmModal(Session s) => new ModalBuilder().WithTitle("Set Pokémon").WithCustomId("pkm_modal")
        .AddTextInput("Pokémon name", "name", placeholder: "e.g. Pikachu", required: true, value: s.SpeciesName)
        .AddTextInput("Level (optional)", "level", placeholder: "1-100", required: false, value: s.Level.ToString())
        .Build();

    private static Modal StatsModal(Session s) => new ModalBuilder().WithTitle("EVs & IVs").WithCustomId("stats_modal")
        .AddTextInput($"{s.Meta?.StatSystem ?? "EV"}s — HP/Atk/Def/SpA/SpD/Spe", "evs", placeholder: "0/0/0/252/0/252", required: false, value: string.Join("/", s.EVs))
        .AddTextInput("IVs — HP/Atk/Def/SpA/SpD/Spe", "ivs", placeholder: "31/31/31/31/31/31", required: false, value: string.Join("/", s.IVs))
        .Build();

    private Modal MovesModal(Session s)
    {
        var b = new ModalBuilder().WithTitle("Moves (type names)").WithCustomId("moves_modal");
        for (int i = 0; i < 4; i++)
            b.AddTextInput($"Move {i + 1}", $"m{i}", required: false, value: s.Moves[i] > 0 ? MoveName(s, s.Moves[i]) : "");
        return b.Build();
    }

    private Modal ItemModal(Session s) => new ModalBuilder().WithTitle("Held Item").WithCustomId("item_modal")
        .AddTextInput("Item name (blank or 'none' = none)", "item", required: false, value: s.HeldItem == 0 ? "" : ItemName(s, s.HeldItem))
        .Build();

    private static Modal ExtrasModal(Session s)
    {
        var b = new ModalBuilder().WithTitle("Extras").WithCustomId("extras_modal")
            .AddTextInput("Nickname (blank = species name)", "nick", required: false, value: s.Nickname)
            .AddTextInput("Friendship (0-255)", "friend", required: false, value: s.Friendship.ToString())
            .AddTextInput("Met Date (YYYY-MM-DD)", "met", required: false, value: s.MetDate);
        if (s.Meta?.HasScale == true)
            b.AddTextInput("Size / Scale (0-255)", "scale", required: false, value: s.Scale.ToString());
        return b.Build();
    }

    private static Modal TrainerModal(Session s) => new ModalBuilder().WithTitle("Custom Trainer").WithCustomId("trainer_modal")
        .AddTextInput("OT Name", "ot", required: true, value: s.OT, maxLength: 12)
        .AddTextInput("TID (0-65535)", "tid", required: false, value: s.TID.ToString())
        .AddTextInput("SID (0-65535)", "sid", required: false, value: s.SID.ToString())
        .Build();

    private string GameName(string id) => _svc.GetGames().FirstOrDefault(g => g.Id == id)?.Name ?? id;
    private string NatureName(int id) => _svc.GetNatures().FirstOrDefault(n => n.Id == id)?.Name ?? id.ToString();
    private static string AbilityName(Session s, int id) => s.AbilityList.FirstOrDefault(a => a.Id == id)?.Name ?? "—";
    private static string BallName(Session s, int id) => s.BallList.FirstOrDefault(b => b.Id == id)?.Name ?? "Poké Ball";
    private string ItemName(Session s, int id) => Items(s.Game).FirstOrDefault(i => i.Id == id)?.Name ?? $"#{id}";
    private static string MoveName(Session s, int id) => s.MoveList.FirstOrDefault(m => m.Id == id)?.Name ?? $"#{id}";
    private static string GenderName(int g) => g switch { 0 => "Male", 1 => "Female", _ => "Genderless" };
    private static string FormNameOf(Session s) => s.SpeciesList.FirstOrDefault(x => x.Id == s.Species && x.Form == s.Form)?.FormName ?? $"Form {s.Form}";
    private static string FormatStats(int[] v) => string.Join(" / ", StatNames.Select((n, i) => $"{n} {v[i]}"));
    private static string SectionLabel(string id) => Sections.FirstOrDefault(x => x.Item1 == id).Item2 ?? id;

    private static Task Stale(SocketInteraction i) => i.RespondAsync("Session expired — click **Create Pokémon** again.", ephemeral: true);

    private sealed class Session
    {
        public string Game = "SV";
        public int Species;
        public string SpeciesName = "";
        public int Form;
        public int Level = 1;
        public bool Shiny, Alpha, UseCustomOT;
        public int Nature, Ability, Gender, Ball = 4, HeldItem, TeraType;
        public int[] Moves = new int[4];
        public int[] EVs = new int[6];
        public int[] IVs = [31, 31, 31, 31, 31, 31];
        public int Friendship = 255, Scale = 128, TID, SID;
        public string MetDate = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        public string OT = "Trainer", Language = "English", Nickname = "";
        public string Section = "pokemon";
        public List<SpeciesInfo> SpeciesList = [];
        public List<AbilityInfo> AbilityList = [];
        public List<ItemInfo> BallList = [];
        public List<MoveEntry> MoveList = [];
        public PokemonMeta? Meta;
        public int StatMax() => Meta?.StatMax ?? 252;
    }
}
