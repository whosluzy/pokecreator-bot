using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using PokecreatorBot.Data;

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
        ("pokemon", "🔹 Nature / Form"),
        ("battle", "⚔️ Ability / Gender / Tera"),
        ("statsmoves", "📊 EVs / IVs / Moves"),
        ("cosmetic", "✨ Size / Friendship / Date / Nickname"),
        ("trainer", "🪪 Trainer / Language"),
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
            case "section": s.Section = v; s.ItemResults = []; s.ItemPage = 0; break;
            case "game":
                ApplyGame(s, v);
                s.Page = 0;
                if (s.Step == 0) { s.SearchResults = []; s.Step = 1; } // game -> pokemon
                break;
            case "pick":
            {
                var bits = v.Split(':');
                int id = int.Parse(bits[0]), form = bits.Length > 1 ? int.Parse(bits[1]) : 0;
                var pick = s.SpeciesList.FirstOrDefault(x => x.Id == id && x.Form == form);
                if (pick != null)
                {
                    s.Species = pick.Id; s.SpeciesName = pick.Name; s.Form = pick.Form;
                    ApplyMeta(s); RefreshLists(s);
                    s.Step = NextAfterPokemon(s);
                }
                break;
            }
            case "form": s.Form = int.Parse(v); ApplyMeta(s); RefreshLists(s); break;
            case "level": s.Level = int.Parse(v); break;
            case "nature": s.Nature = int.Parse(v); break;
            case "ability": s.Ability = int.Parse(v); break;
            case "gender": s.Gender = int.Parse(v); break;
            case "ball": s.Ball = int.Parse(v); break;
            case "tera": s.TeraType = int.Parse(v); break;
            case "language": s.Language = v; break;
            case "move_pick":
                if (s.MoveSlot is >= 0 and < 4) s.Moves[s.MoveSlot] = int.Parse(v);
                s.MoveResults = [];
                break;
            case "item_pick":
                s.HeldItem = int.Parse(v);
                s.ItemResults = [];
                break;
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
            var fresh = new Session { Step = 0 };   // no game pre-selected
            _sessions[c.User.Id] = fresh;
            await c.RespondAsync(embed: BuildEmbed(fresh), components: BuildComponents(fresh), ephemeral: true);
            return;
        }

        if (!_sessions.TryGetValue(c.User.Id, out var s)) { await Stale(c); return; }
        switch (c.Data.CustomId)
        {
            // ── wizard steps ──
            case "wiz_search": await c.RespondWithModalAsync(SearchModal()); return;
            case "wiz_back": s.Step = PrevStep(s); break;
            case "wiz_next": s.Step = NextStep(s); break;
            case "open_extras": s.Section = "pokemon"; s.Step = 8; break;

            // ── Pokémon list paging ──
            case "pg_prev": s.Page--; break;
            case "pg_next": s.Page++; break;
            case "pg_clear": s.SearchResults = []; s.Page = 0; break;

            // ── held-item list paging ──
            case "item_pg_prev": s.ItemPage--; break;
            case "item_pg_next": s.ItemPage++; break;
            case "shiny_yes":
                s.Shiny = true;
                if (s.Meta is { ShinyMinLevel: > 0 } && s.Level < s.Meta.ShinyMinLevel)
                    s.Level = s.Meta.ShinyMinLevel;   // e.g. shiny Koraidon → 100
                s.Step = NextAfterShiny(s);
                break;
            case "shiny_no": s.Shiny = false; s.Step = NextAfterShiny(s); break;
            case "shiny_continue": s.Shiny = false; s.Step = NextAfterShiny(s); break;
            case "alpha_yes":
                s.Alpha = true;
                if (s.Meta?.AlphaMinLevel > 0) s.Level = s.Meta.AlphaMinLevel;
                s.Step = 4;
                break;
            case "alpha_no": s.Alpha = false; s.Step = 4; break;

            // ── customize controls ──
            case "set_pkm": await c.RespondWithModalAsync(PkmModal(s)); return;
            case "edit_stats": await c.RespondWithModalAsync(StatsModal(s)); return;
            case "m0": case "m1": case "m2": case "m3":
                s.MoveSlot = c.Data.CustomId[1] - '0';
                s.MoveResults = [];
                await c.RespondWithModalAsync(SearchModalFor("move_search", $"Search move for slot {s.MoveSlot + 1}"));
                return;
            case "item_search":
                s.ItemResults = [];
                await c.RespondWithModalAsync(SearchModalFor("item_search", "Search held item"));
                return;
            case "item_clear": s.HeldItem = 0; s.ItemResults = []; s.ItemPage = 0; break;
            case "edit_extras": await c.RespondWithModalAsync(ExtrasModal(s)); return;
            case "edit_trainer": await c.RespondWithModalAsync(TrainerModal(s)); return;
            case "shiny": if (s.Meta?.CanBeShiny == true) s.Shiny = !s.Shiny; break;
            case "alpha": if (s.Meta?.HasAlpha == true) s.Alpha = !s.Alpha; break;
            case "customot": s.UseCustomOT = !s.UseCustomOT; break;
            case "generate":
            {
                if (s.Species == 0) { await c.RespondAsync("Pick a Pokémon first.", ephemeral: true); return; }
                await c.DeferAsync(ephemeral: true);

                // Run it through the AutoLegality Mod. Illegal combinations are refused here.
                var result = _svc.GenerateLegal(BuildConfig(s));
                if (!result.Ok)
                {
                    await c.FollowupAsync($"❌ **Can't create that legally.**\n{result.Report}", ephemeral: true);
                    return;
                }

                // Instruction first, then the format ALONE in its own message so it copies cleanly.
                await c.FollowupAsync(
                    $"📋 Copy the format below and paste it into the **{GameName(s.Game)}** bot channel, then send it to request this Pokémon:",
                    ephemeral: true);
                await c.FollowupAsync(result.TradeText, ephemeral: true);

                // Also attach the ready-made legal file for direct use.
                if (result.File is { Length: > 0 } bytes && result.FileName is { } fn)
                {
                    using var ms = new MemoryStream(bytes);
                    await c.FollowupWithFileAsync(ms, fn,
                        text: "✅ Verified legal. You can also drop this file straight into a trade bot.",
                        ephemeral: true);
                }
                return;
            }
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
            case "search_modal":
            {
                var q = Val("q").ToLowerInvariant();
                s.SearchResults = s.SpeciesList
                    .Where(x => x.Name.ToLowerInvariant().Contains(q)
                             || (x.FormName?.ToLowerInvariant().Contains(q) ?? false))
                    .ToList();
                s.Page = 0;
                if (s.SearchResults.Count == 1)
                {
                    var pick = s.SearchResults[0];
                    s.Species = pick.Id; s.SpeciesName = pick.Name; s.Form = pick.Form;
                    ApplyMeta(s); RefreshLists(s);
                    s.Step = NextAfterPokemon(s);
                }
                break;
            }
            case "move_search":
            {
                var q = Val("q").ToLowerInvariant();
                s.MoveResults = s.MoveList.Where(x => x.Name.ToLowerInvariant().Contains(q)).Take(25).ToList();
                if (s.MoveResults.Count == 1 && s.MoveSlot is >= 0 and < 4)
                {
                    s.Moves[s.MoveSlot] = s.MoveResults[0].Id;
                    s.MoveResults = [];
                }
                break;
            }
            case "item_search":
            {
                var q = Val("q").ToLowerInvariant();
                s.ItemResults = Items(s.Game).Where(x => x.Name.ToLowerInvariant().Contains(q)).ToList();
                s.ItemPage = 0;
                if (s.ItemResults.Count == 1)
                {
                    s.HeldItem = s.ItemResults[0].Id;
                    s.ItemResults = [];
                }
                break;
            }
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
            {
                // Only mark EVs/IVs as "set" (and thus shown in the format) when the user
                // actually typed values — a blank field leaves them at defaults and off the format.
                var evsIn = Val("evs"); var ivsIn = Val("ivs");
                if (!string.IsNullOrWhiteSpace(evsIn)) { s.EVs = ParseStats(evsIn, s.StatMax(), s.EVs); s.EVsSet = true; }
                if (!string.IsNullOrWhiteSpace(ivsIn)) { s.IVs = ParseStats(ivsIn, 31, s.IVs); s.IVsSet = true; }
                ClampEvTotal(s);
                break;
            }
            case "moves_modal":
                for (int i = 0; i < 4; i++) s.Moves[i] = ResolveMove(s, Val($"m{i}"));
                break;
            case "item_modal":
                s.HeldItem = ResolveItem(s, Val("item"));
                break;
            case "extras_modal":
                s.Nickname = Val("nick");
                if (int.TryParse(Val("scale"), out var sc)) s.Scale = Math.Clamp(sc, 0, 255);
                // These three are only "set" when the user actually enters a value.
                if (int.TryParse(Val("friend"), out var fr)) { s.Friendship = Math.Clamp(fr, 0, 255); s.FriendshipSet = true; }
                if (DateOnly.TryParse(Val("met"), out var d)) { s.MetDate = d.ToString("yyyy-MM-dd"); s.MetDateSet = true; }
                if (int.TryParse(Val("dmax"), out var dm)) { s.DynamaxLevel = Math.Clamp(dm, 0, 10); s.DynamaxSet = true; }
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

    // ── wizard step flow ──
    private static bool AlphaApplicable(Session s) => s.Meta is { HasAlpha: true, AlphaMinLevel: > 0 };
    // Always visit the shiny step — it shows Yes/No, or a "no shiny version" notice.
    private static int NextAfterPokemon(Session s) => 2;
    private static int NextAfterShiny(Session s) => AlphaApplicable(s) ? 3 : 4;
    // Forward "Next ▶" through the guided level → item → ball → finalize steps.
    private static int NextStep(Session s) => s.Step switch
    {
        4 => 5,   // level → held item
        5 => 6,   // held item → ball
        6 => 7,   // ball → finalize
        _ => s.Step,
    };
    private static int PrevStep(Session s) => s.Step switch
    {
        8 => 7,   // extras → finalize
        7 => 6,   // finalize → ball
        6 => 5,   // ball → held item
        5 => 4,   // held item → level
        4 => AlphaApplicable(s) ? 3 : 2,   // level → alpha (if any) else shiny
        3 => 2,
        2 => 1,
        1 => 0,
        _ => 0,
    };

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
        EVsSet = s.EVsSet, IVsSet = s.IVsSet,
        Friendship = s.Friendship, FriendshipSet = s.FriendshipSet,
        Scale = s.Scale, MetDate = s.MetDate, MetDateSet = s.MetDateSet,
        DynamaxLevel = s.DynamaxLevel, DynamaxSet = s.DynamaxSet,
        UseCustomOT = s.UseCustomOT, OT = s.OT, TID = s.TID, SID = s.SID,
        Language = s.Language, Nickname = s.Nickname, TeraType = s.TeraType,
    };

    // Plain text (no code fences) so copy/paste is clean — no ``` and nothing else.
    private string BuildTradeText(Session s) => _svc.ToShowdown(BuildConfig(s));

    // ───────────────── UI ─────────────────

    private Embed BuildEmbed(Session s)
    {
        var formTxt = s.Form > 0 ? $" ({FormNameOf(s)})" : "";
        var chosen = s.Species == 0 ? "—" : $"{s.SpeciesName}{formTxt}";

        // Guided steps get a focused embed.
        if (s.Step <= 6)
        {
            var eb2 = new EmbedBuilder().WithColor(new Color(0x7c, 0x3a, 0xed));
            switch (s.Step)
            {
                case 0:
                    eb2.WithTitle("Step 1 · Choose a Game")
                       .WithDescription("Which game is this Pokémon for? Pick one below.");
                    break;
                case 1:
                    eb2.WithTitle("Step 2 · Choose your Pokémon")
                       .WithDescription($"**Game:** {GameName(s.Game)}\n\nBrowse the list with **◀ Prev / Next ▶**, or **🔍 Search** by name. Every Pokémon available in this game is here.");
                    if (s.SearchResults.Count > 0)
                        eb2.AddField($"Search matches ({s.SearchResults.Count})",
                            string.Join(", ", s.SearchResults.Take(10).Select(x => x.Name + (x.FormName != null ? $" ({x.FormName})" : "")))
                            + (s.SearchResults.Count > 10 ? " …" : ""), false);
                    break;
                case 2:
                    if (s.Meta?.CanBeShiny == true)
                        eb2.WithTitle("Step 3 · Shiny?")
                           .WithDescription($"**{chosen}** in **{GameName(s.Game)}**\n\nDo you want it **Shiny** ✨?");
                    else
                        eb2.WithTitle("Step 3 · Shiny")
                           .WithDescription($"✨ **This Pokémon does not exist in a shiny version.**\n\nContinue to finish building **{chosen}**.");
                    break;
                case 3:
                    eb2.WithTitle("Step 4 · Alpha?")
                       .WithDescription($"**{chosen}** can be an **Alpha** α in this game.\n\nMake it an Alpha?");
                    break;
                case 4:
                {
                    var (lo, hi) = LevelRange(s);
                    if (s.Level < lo) s.Level = lo;
                    eb2.WithTitle("Step 5 · Choose Level")
                       .WithDescription($"**{chosen}** — pick a level (**{lo}–{hi}**).\nCurrent: **Lv {s.Level}**\n\nThen press **Next ▶**.");
                    break;
                }
                case 5:
                    eb2.WithTitle("Step 6 · Held Item")
                       .WithDescription($"Give **{chosen}** a held item (optional).\nCurrent: **{(s.HeldItem == 0 ? "None" : ItemName(s, s.HeldItem))}**\n\nBrowse or 🔍 search, or just press **Next ▶** to skip.");
                    break;
                case 6:
                    eb2.WithTitle("Step 7 · Ball Caught In")
                       .WithDescription($"Which Ball was **{chosen}** caught in?\nCurrent: **{BallName(s, s.Ball)}**\n\nThen press **Next ▶**.");
                    break;
            }
            eb2.WithFooter("You can go ◀ Back anytime.");
            return eb2.Build();
        }

        // Final step: full build summary.
        var statLabel = s.Meta?.StatSystem ?? "EV";
        var moves = string.Join(", ", s.Moves.Where(m => m > 0).Select(id => MoveName(s, id)));
        var eb = new EmbedBuilder()
            .WithTitle($"🛠️ Customize — {chosen}")
            .WithColor(new Color(0x7c, 0x3a, 0xed))
            .AddField("Game", GameName(s.Game), true)
            .AddField("Pokémon", chosen, true)
            .AddField("Level", s.Level.ToString(), true)
            .AddField("Shiny", s.Shiny ? "✨ Yes" : "No", true)
            .AddField("Alpha", s.Meta?.HasAlpha == true ? (s.Alpha ? "α Yes" : "No") : "N/A", true)
            .AddField("Nature", NatureName(s.Nature), true)
            .AddField("Ability", AbilityName(s, s.Ability), true)
            .AddField("Gender", GenderName(s.Gender), true)
            .AddField("Ball Caught", BallName(s, s.Ball), true)
            .AddField("Held Item", s.HeldItem == 0 ? "None" : ItemName(s, s.HeldItem), true)
            .AddField(statLabel + "s", FormatStats(s.EVs), true)
            .AddField("IVs", FormatStats(s.IVs), true)
            .AddField("Moves", string.IsNullOrEmpty(moves) ? "—" : moves, false)
            .AddField("Friendship", s.Friendship.ToString(), true)
            .AddField("Met Date", s.MetDate, true)
            .AddField("Trainer", s.UseCustomOT ? $"{s.OT} ({s.TID}/{s.SID})" : "AutoOT", true);

        if (s.Meta?.HasTeraType == true) eb.AddField("Tera", TeraTypes.ElementAtOrDefault(s.TeraType) ?? "—", true);
        if (s.Meta?.HasScale == true) eb.AddField("Size", $"{s.Scale}", true);
        eb.WithTitle(s.Step == 8 ? $"✨ Extras — {chosen}" : $"✅ Ready — {chosen}");
        eb.WithFooter(s.Step == 8
            ? "Adjust anything optional, then ◀ Back to finish."
            : "Everything required is set. Press ✨ Extras to fine-tune, or ⚡ Get Bot Ready Format.");
        return eb.Build();
    }

    private MessageComponent BuildComponents(Session s) => s.Step switch
    {
        0 => new ComponentBuilder().WithSelectMenu(GameMenu(s), 0).Build(),
        1 => BuildPokemonStep(s),
        2 => BuildShinyStep(s),
        3 => new ComponentBuilder()
                .WithButton("α Yes, Alpha", "alpha_yes", ButtonStyle.Success, row: 0)
                .WithButton("No", "alpha_no", ButtonStyle.Secondary, row: 0)
                .WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 1)
                .Build(),
        4 => BuildLevelStep(s),
        5 => BuildItemStep(s),
        6 => BuildBallStep(s),
        7 => BuildFinalizeStep(s),
        _ => BuildCustomizeComponents(s),   // 8 = Extras (everything else)
    };

    private static MessageComponent BuildShinyStep(Session s)
    {
        var b = new ComponentBuilder();
        if (s.Meta?.CanBeShiny == true)
        {
            b.WithButton("✨ Yes, Shiny", "shiny_yes", ButtonStyle.Success, row: 0);
            b.WithButton("No", "shiny_no", ButtonStyle.Secondary, row: 0);
        }
        else
        {
            b.WithButton("Continue ▶", "shiny_continue", ButtonStyle.Primary, row: 0);
        }
        b.WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 1);
        return b.Build();
    }

    // Step 5 — Level.
    private MessageComponent BuildLevelStep(Session s)
    {
        var (min, _) = LevelRange(s);
        if (s.Level < min) s.Level = min;       // keep the pick inside the legal window
        var b = new ComponentBuilder();
        b.WithSelectMenu(LevelMenu(s), 0);
        b.WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 1);
        b.WithButton("Next ▶", "wiz_next", ButtonStyle.Primary, row: 1);
        return b.Build();
    }

    // Step 6 — Held item (optional; paged + searchable list).
    private MessageComponent BuildItemStep(Session s)
    {
        var b = new ComponentBuilder();
        b.WithSelectMenu(ItemPickMenu(s), 0);
        b.WithButton("◀ Prev", "item_pg_prev", ButtonStyle.Secondary, row: 1, disabled: s.ItemPage == 0);
        b.WithButton("Next ▶", "item_pg_next", ButtonStyle.Secondary, row: 1, disabled: s.ItemPage >= ItemPageCount(s) - 1);
        b.WithButton("🔍 Search", "item_search", ButtonStyle.Primary, row: 1);
        b.WithButton("Clear", "item_clear", ButtonStyle.Secondary, row: 1);
        b.WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 2);
        b.WithButton(s.HeldItem == 0 ? "Skip ▶" : "Next ▶", "wiz_next", ButtonStyle.Primary, row: 2);
        return b.Build();
    }

    // Step 7 — Ball caught in.
    private MessageComponent BuildBallStep(Session s)
    {
        var b = new ComponentBuilder();
        b.WithSelectMenu(BallMenu(s), 0);
        b.WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 1);
        b.WithButton("Next ▶", "wiz_next", ButtonStyle.Primary, row: 1);
        return b.Build();
    }

    // Finalize — everything required is set; the rest is optional behind Extras.
    private MessageComponent BuildFinalizeStep(Session s)
    {
        var b = new ComponentBuilder();
        b.WithButton("✨ Extras (optional)", "open_extras", ButtonStyle.Secondary, row: 0);
        b.WithButton("⚡ Get Bot Ready Format", "generate", ButtonStyle.Success, disabled: s.Species == 0, row: 0);
        b.WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 1);
        return b.Build();
    }

    private const int PokePageSize = 25;

    private MessageComponent BuildPokemonStep(Session s)
    {
        var b = new ComponentBuilder();
        // Full browsable list (every Pokémon in the game), in National Dex order.
        // A search narrows the list; pages let you scroll through all of them.
        var list = (s.SearchResults.Count > 0 ? s.SearchResults : s.SpeciesList)
            .OrderBy(x => x.Id).ThenBy(x => x.Form).ToList();

        int totalPages = Math.Max(1, (list.Count + PokePageSize - 1) / PokePageSize);
        s.Page = Math.Clamp(s.Page, 0, totalPages - 1);
        var pageItems = list.Skip(s.Page * PokePageSize).Take(PokePageSize).ToList();

        if (pageItems.Count > 0)
        {
            var pick = new SelectMenuBuilder().WithCustomId("pick")
                .WithPlaceholder($"Pick your Pokémon… (page {s.Page + 1}/{totalPages})");
            foreach (var r in pageItems)
            {
                // Pikachu's alternate forms are hats/caps → tag "Hat" instead of HOME.
                string tag = r.Id == 25 && r.Form > 0 ? " (Hat)" : r.Native ? "" : " ⇄HOME";
                var label = r.Name + (r.FormName != null ? $" ({r.FormName})" : "") + tag;
                pick.AddOption(label.Length > 100 ? label[..100] : label, $"{r.Id}:{r.Form}");
            }
            b.WithSelectMenu(pick, 0);
        }

        // Pager controls.
        b.WithButton("◀ Prev", "pg_prev", ButtonStyle.Secondary, row: 1, disabled: s.Page == 0);
        b.WithButton($"Page {s.Page + 1}/{totalPages}", "pg_noop", ButtonStyle.Secondary, row: 1, disabled: true);
        b.WithButton("Next ▶", "pg_next", ButtonStyle.Secondary, row: 1, disabled: s.Page >= totalPages - 1);

        b.WithButton("🔍 Search", "wiz_search", ButtonStyle.Primary, row: 2);
        if (s.SearchResults.Count > 0)
            b.WithButton("Clear search", "pg_clear", ButtonStyle.Secondary, row: 2);
        b.WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 2);
        return b.Build();
    }

    private MessageComponent BuildCustomizeComponents(Session s)
    {
        var b = new ComponentBuilder();
        var sec = new SelectMenuBuilder().WithCustomId("section").WithPlaceholder("More options…");
        foreach (var (id, label) in Sections) sec.AddOption(label, id, isDefault: id == s.Section);
        b.WithSelectMenu(sec, 0);

        int r = 1;
        switch (s.Section)
        {
            case "pokemon":
                if (Forms(s).Count > 1) b.WithSelectMenu(FormMenu(s), r++);
                b.WithSelectMenu(NatureMenu(s), r++);
                b.WithButton("Change Pokémon", "set_pkm", ButtonStyle.Secondary, row: 4);
                break;
            case "battle":
                b.WithSelectMenu(AbilityMenu(s), r++);
                b.WithSelectMenu(GenderMenu(s), r++);
                if (s.Meta?.HasTeraType == true) b.WithSelectMenu(TeraMenu(s), r++);
                break;
            case "statsmoves":
                if (s.MoveResults.Count > 0) b.WithSelectMenu(MovePickMenu(s), r++);
                b.WithButton("EVs / IVs", "edit_stats", ButtonStyle.Primary, row: 3);
                b.WithButton("Move 1", "m0", ButtonStyle.Secondary, row: 3);
                b.WithButton("Move 2", "m1", ButtonStyle.Secondary, row: 3);
                b.WithButton("Move 3", "m2", ButtonStyle.Secondary, row: 3);
                b.WithButton("Move 4", "m3", ButtonStyle.Secondary, row: 3);
                break;
            case "cosmetic":
                b.WithButton("Size / Friendship / Date / Nickname", "edit_extras", ButtonStyle.Primary, row: 3);
                break;
            case "trainer":
                b.WithSelectMenu(LanguageMenu(s), r++);
                b.WithButton(s.UseCustomOT ? "Custom OT: ON" : "AutoOT", "customot",
                    s.UseCustomOT ? ButtonStyle.Success : ButtonStyle.Secondary, row: 3);
                b.WithButton("Edit OT / TID / SID", "edit_trainer", ButtonStyle.Primary, row: 3);
                break;
        }
        _ = r;
        b.WithButton("◀ Back", "wiz_back", ButtonStyle.Secondary, row: 4);
        b.WithButton("⚡ Get Bot Ready Format", "generate", ButtonStyle.Success, disabled: s.Species == 0, row: 4);
        return b.Build();
    }

    private static Modal SearchModal() => new ModalBuilder().WithTitle("Search Pokémon").WithCustomId("search_modal")
        .AddTextInput("Type a name (or part of it)", "q", placeholder: "e.g. char, pika, lucario", required: true)
        .Build();

    private static Modal SearchModalFor(string id, string title) => new ModalBuilder()
        .WithTitle(title.Length > 45 ? title[..45] : title).WithCustomId(id)
        .AddTextInput("Type a name (or part of it)", "q", placeholder: "start typing…", required: true)
        .Build();

    private SelectMenuBuilder MovePickMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("move_pick").WithPlaceholder($"Pick move for slot {s.MoveSlot + 1}…");
        m.AddOption("— None —", "0");
        foreach (var mv in s.MoveResults.Take(24))
            m.AddOption(mv.Name.Length > 100 ? mv.Name[..100] : mv.Name, mv.Id.ToString());
        return m;
    }

    private const int ItemPageSize = 24;   // 24 + the "— None —" option = 25 (Discord max)

    private int ItemPageCount(Session s)
    {
        var list = s.ItemResults.Count > 0 ? s.ItemResults : Items(s.Game);
        return Math.Max(1, (list.Count + ItemPageSize - 1) / ItemPageSize);
    }

    private SelectMenuBuilder ItemPickMenu(Session s)
    {
        // Full browsable, paged item list (A–Z). A search narrows it; pages scroll through all.
        var list = (s.ItemResults.Count > 0 ? s.ItemResults : Items(s.Game))
            .OrderBy(x => x.Name).ToList();
        int totalPages = ItemPageCount(s);
        s.ItemPage = Math.Clamp(s.ItemPage, 0, totalPages - 1);

        var m = new SelectMenuBuilder().WithCustomId("item_pick")
            .WithPlaceholder($"Pick held item… (page {s.ItemPage + 1}/{totalPages})");
        m.AddOption("— None —", "0");
        foreach (var it in list.Skip(s.ItemPage * ItemPageSize).Take(ItemPageSize))
            m.AddOption(it.Name.Length > 100 ? it.Name[..100] : it.Name, it.Id.ToString());
        return m;
    }

    // Games hidden from the bot for now (data/logic kept intact, just not listed).
    private static readonly HashSet<string> HiddenGames = ["PLA", "LGLE"];

    private SelectMenuBuilder GameMenu(Session s)
    {
        var m = new SelectMenuBuilder().WithCustomId("game").WithPlaceholder("Game");
        foreach (var g in _svc.GetGames())
        {
            if (HiddenGames.Contains(g.Id)) continue;
            m.AddOption(g.Name, g.Id, isDefault: g.Id == s.Game);
        }
        return m;
    }
    // Legal level window for the current pick: evolution-aware floor (alpha/shiny raise it);
    // ceiling is always 100 so any final evolution can be lifted to 100.
    private static (int Min, int Max) LevelRange(Session s)
    {
        int min = s.Alpha && s.Meta?.AlphaMinLevel > 0 ? s.Meta.AlphaMinLevel
                : s.Meta?.MinLevel > 0 ? s.Meta.MinLevel : 1;
        if (s.Shiny && s.Meta is { ShinyMinLevel: > 0 } && s.Meta.ShinyMinLevel > min)
            min = s.Meta.ShinyMinLevel;
        int max = Math.Max(100, min);
        return (min, max);
    }

    private static SelectMenuBuilder LevelMenu(Session s)
    {
        var (min, max) = LevelRange(s);

        var levels = new List<int> { min };
        for (int l = (min / 5 + 1) * 5; l <= max; l += 5) levels.Add(l);
        if (!levels.Contains(max)) levels.Add(max);
        levels = levels.Where(l => l >= min && l <= max).Distinct().OrderBy(x => x).Take(25).ToList();

        var m = new SelectMenuBuilder().WithCustomId("level").WithPlaceholder($"Level ({min}–{max})");
        foreach (var l in levels) m.AddOption($"Level {l}", l.ToString(), isDefault: l == s.Level);
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
        var m = new SelectMenuBuilder().WithCustomId("ball").WithPlaceholder("Ball Caught:");
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


    private static Modal ExtrasModal(Session s)
    {
        // Friendship / Met Date / Dynamax are left BLANK on purpose — they are only
        // added to the output if the user actually fills them in.
        var b = new ModalBuilder().WithTitle("Extras").WithCustomId("extras_modal")
            .AddTextInput("Nickname (blank = species name)", "nick", required: false, value: s.Nickname)
            .AddTextInput("Friendship (blank = leave default)", "friend", required: false,
                value: s.FriendshipSet ? s.Friendship.ToString() : "", placeholder: "0-255")
            .AddTextInput("Met Date (blank = leave default)", "met", required: false,
                value: s.MetDateSet ? s.MetDate : "", placeholder: "YYYY-MM-DD");
        if (s.Game == "SWSH")
            b.AddTextInput("Dynamax Level (blank = leave default)", "dmax", required: false,
                value: s.DynamaxSet ? s.DynamaxLevel.ToString() : "", placeholder: "0-10");
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
        public string Game = "";          // empty until the user picks — no pre-selection
        public int Page;                  // current page in the browsable Pokémon list
        public int ItemPage;              // current page in the browsable held-item list
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
        public bool FriendshipSet, MetDateSet, DynamaxSet, EVsSet, IVsSet;
        public int DynamaxLevel;
        public string MetDate = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        public string OT = "Trainer", Language = "English", Nickname = "";
        public string Section = "pokemon";
        public int Step;                       // 0 game,1 pokemon,2 shiny,3 alpha,4 level,5 item,6 ball,7 finalize,8 extras
        public List<SpeciesInfo> SearchResults = [];
        public int MoveSlot;                   // which move slot (0-3) the search targets
        public List<MoveEntry> MoveResults = [];
        public List<ItemInfo> ItemResults = [];
        public List<SpeciesInfo> SpeciesList = [];
        public List<AbilityInfo> AbilityList = [];
        public List<ItemInfo> BallList = [];
        public List<MoveEntry> MoveList = [];
        public PokemonMeta? Meta;
        public int StatMax() => Meta?.StatMax ?? 252;
    }
}
