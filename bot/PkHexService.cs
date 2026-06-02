using PKHeX.Core;

namespace PokecreatorBot.Data;

public class PkHexService
{
    private static readonly Dictionary<string, GameVersion> GameMap = new()
    {
        ["SV"]   = GameVersion.SL,
        ["SWSH"] = GameVersion.SW,
        ["PLA"]  = GameVersion.PLA,
        ["BDSP"] = GameVersion.BD,
        ["LGLE"] = GameVersion.GP,
        ["ZA"]   = GameVersion.ZA,
    };

    private static readonly Dictionary<string, EntityContext> ContextMap = new()
    {
        ["SV"]   = EntityContext.Gen9,
        ["SWSH"] = EntityContext.Gen8,
        ["PLA"]  = EntityContext.Gen8a,
        ["BDSP"] = EntityContext.Gen8b,
        ["LGLE"] = EntityContext.Gen7b,
        ["ZA"]   = EntityContext.Gen9a,
    };

    private static readonly Dictionary<string, string> GameNames = new()
    {
        ["ZA"]   = "Legends: Z-A",
        ["SV"]   = "Scarlet / Violet",
        ["SWSH"] = "Sword / Shield",
        ["PLA"]  = "Legends: Arceus",
        ["BDSP"] = "Brilliant Diamond / Shining Pearl",
        ["LGLE"] = "Let's Go Pikachu / Eevee",
    };

    // All HOME-connected source games. An entity is legal in a target game if an
    // encounter from any of these can be converted into the target game's format.
    private static readonly GameVersion[] TransferVersions =
    {
        GameVersion.SL, GameVersion.VL, GameVersion.ZA,   // Gen 9 + Legends Z-A
        GameVersion.SW, GameVersion.SH,                    // Sword / Shield
        GameVersion.BD, GameVersion.SP,                    // BDSP
        GameVersion.PLA,                                   // Legends: Arceus
        GameVersion.GP, GameVersion.GE,                    // Let's Go
    };

    private readonly GameStrings _strings;

    public PkHexService()
    {
        _strings = GameInfo.GetStrings("en");
    }

    public List<GameEntry> GetGames() =>
        GameNames.Select(g => new GameEntry(g.Key, g.Value)).ToList();

    private readonly Dictionary<string, List<SpeciesInfo>> _speciesCache = new();

    public List<SpeciesInfo> GetAllSpecies(string game)
    {
        if (_speciesCache.TryGetValue(game, out var cached))
            return cached;

        if (!GameMap.TryGetValue(game, out var version))
            throw new ArgumentException($"Unknown game: {game}");

        var personal = GameData.GetPersonal(version);
        var context = ContextMap[game];
        var result = new List<SpeciesInfo>();
        var nativeVersions = new GameVersion[] { version };

        for (int i = 1; i < _strings.Species.Count; i++)
        {
            var name = _strings.Species[i];
            if (string.IsNullOrWhiteSpace(name)) continue;

            // National-dex / present pre-filter for the base species.
            var pi0 = personal.GetFormEntry((ushort)i, 0);
            bool present = pi0 switch
            {
                PersonalInfo9SV   sv9 => sv9.IsPresentInGame,
                PersonalInfo9ZA   za9 => za9.IsPresentInGame,
                PersonalInfo8SWSH sw8 => sw8.IsPresentInGame,
                PersonalInfo8LA   la8 => la8.IsPresentInGame,
                PersonalInfo8BDSP _   => i <= 493,
                PersonalInfo7GG   _   => (i >= 1 && i <= 151) || i == 808 || i == 809,
                _ => pi0.HP > 0,
            };
            if (!present) continue;

            // Form names for this species (regional/alternate forms).
            string[] formNames;
            try { formNames = FormConverter.GetFormList((ushort)i, _strings.Types, _strings.forms, context); }
            catch { formNames = []; }

            int formCount = Math.Max(1, (int)pi0.FormCount);

            for (byte f = 0; f < formCount; f++)
            {
                // Only show: regional forms (Alola / Galar / Hisui / Paldea) and
                // cosmetic colour/pattern variants. Every other form (Therian,
                // Deoxys, Rotom appliances, Mega/Gigantamax, Zen, fusions, …) is an
                // in-game form change and must NOT be offered here.
                if (f > 0)
                {
                    string fname = (f < formNames.Length ? formNames[f] : null) ?? "";

                    bool isRegional =
                        fname.Contains("Alola", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("Galar", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("Hisui", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("Paldea", StringComparison.OrdinalIgnoreCase);

                    // Battle-only / transient / event-costume forms — never selectable here.
                    string[] blocked =
                    [
                        "Mega", "Primal", "Gigantamax", "Eternamax", "Busted", "Gorging",
                        "Gulping", "Hangry", "Noice", "Crowned", "Ash", "Eternal", "Bond",
                        "Original", "Hoenn", "Sinnoh", "Unova", "Kalos", "World", "Partner",
                        "Starter", "Cosplay", "Cap",
                    ];
                    bool special = blocked.Any(x => fname.Contains(x, StringComparison.OrdinalIgnoreCase));

                    var pif = personal.GetFormEntry((ushort)i, f);
                    bool isCosmetic =
                        pif.Type1 == pi0.Type1 && pif.Type2 == pi0.Type2 &&
                        pif.HP == pi0.HP && pif.ATK == pi0.ATK && pif.DEF == pi0.DEF &&
                        pif.SPA == pi0.SPA && pif.SPD == pi0.SPD && pif.SPE == pi0.SPE;

                    if (special || (!isRegional && !isCosmetic)) continue;
                }

                // Offer the form only if it has a legal encounter in this game,
                // natively or via HOME transfer. (Mega/battle-only forms have no
                // encounter and are excluded automatically.)
                var pk = CreateBlankPkm(version);
                pk.Species = (ushort)i;
                pk.Form = f;
                pk.Version = version;

                bool native = EncounterMovesetGenerator
                    .GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, nativeVersions)
                    .Any();
                bool legal = native || EncounterMovesetGenerator
                    .GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, TransferVersions)
                    .Any();
                if (!legal) continue;

                string? formName = (f > 0 && f < formNames.Length) ? formNames[f] : null;
                result.Add(new SpeciesInfo(i, name, native, f, formName));
            }
        }

        _speciesCache[game] = result;
        return result;
    }

    public List<EncounterDetail> GetEncounters(string game, int species, int form = 0)
    {
        if (!GameMap.TryGetValue(game, out var version))
            throw new ArgumentException($"Unknown game: {game}");

        var pk = CreateBlankPkm(version);
        pk.Species = (ushort)species;
        pk.Form = (byte)form;
        pk.Version = version;

        // Native encounters first.
        var encounters = EncounterMovesetGenerator
            .GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, new GameVersion[] { version })
            .ToList();

        // HOME-transfer only: no native encounter → pull legal encounters from
        // every connected game (converted into this game's format).
        if (encounters.Count == 0)
        {
            encounters = EncounterMovesetGenerator
                .GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, TransferVersions)
                .ToList();
        }

        return encounters.Select(e => BuildEncounterDetail(e, pk, version)).ToList();
    }

    private EncounterDetail BuildEncounterDetail(IEncounterable e, PKM pk, GameVersion version)
    {
        // ── Location ──────────────────────────────────────────────────────────
        string? loc = null;
        if (e is ILocation locEnc)
        {
            try { loc = _strings.GetLocationName(false, (ushort)locEnc.Location, pk.Format, pk.Format, version); }
            catch { }
        }

        // ── Type label ────────────────────────────────────────────────────────
        string typeName = e.GetType().Name;
        string type = typeName switch
        {
            _ when typeName.StartsWith("WC")                    => "Event",
            _ when typeName.Contains("Slot9a")                  => "Wild",
            _ when typeName.Contains("Static9a")                => "Static",
            _ when typeName.Contains("Gift9a")                  => "Gift",
            _ when typeName.Contains("Trade9a")                 => "Trade",
            _ when typeName.Contains("Slot8a")                  => "Wild",
            _ when typeName.Contains("Static8a")                => "Static",
            _ when typeName.Contains("Slot")                    => "Wild",
            _ when typeName.Contains("Static")                  => "Static",
            _ when typeName.Contains("Gift")                    => "Gift",
            _ when typeName.Contains("Trade")                   => "Trade",
            _ when typeName.Contains("Egg")                     => "Egg",
            _ when typeName.Contains("Outbreak")                => "Outbreak",
            _ when typeName.Contains("Tera")                    => "Tera Raid",
            _ when typeName.Contains("Dist")                    => "Distribution Raid",
            _ when typeName.Contains("Might")                   => "Mighty Raid",
            _ when typeName.Contains("Fixed")                   => "Static",
            _ => typeName.Replace("Encounter","")
        };

        // ── Shiny ─────────────────────────────────────────────────────────────
        string shiny = e.Shiny.ToString();

        // ── Alpha ─────────────────────────────────────────────────────────────
        bool isAlpha = e is EncounterSlot9a { IsAlpha: true }
                    || e is EncounterStatic9a { IsAlpha: true };

        // ── Gender ────────────────────────────────────────────────────────────
        // Grab raw Gender property via reflection — varies by encounter type
        int? fixedGender = null;
        {
            var gProp = e.GetType().GetProperty("Gender");
            if (gProp != null)
            {
                var raw = gProp.GetValue(e);
                int g = raw is Enum ? (int)Convert.ToInt32(raw) : raw is byte b ? b : -1;
                // 255 = any (byte overflow), 0 = male, 1 = female, 2 = genderless
                if (g is 0 or 1 or 2)
                    fixedGender = g;
            }
        }

        // ── Nature ────────────────────────────────────────────────────────────
        int? fixedNature = null;
        {
            var nProp = e.GetType().GetProperty("Nature");
            if (nProp != null)
            {
                var raw = nProp.GetValue(e);
                if (raw != null)
                {
                    int n = Convert.ToInt32(raw);
                    if (n is >= 0 and <= 24) fixedNature = n; // 25 = Random
                }
            }
        }

        // ── IVs ───────────────────────────────────────────────────────────────
        int?[] fixedIVs = [null, null, null, null, null, null];
        int flawlessIVCount = 0;
        {
            var ivsProp = e.GetType().GetProperty("IVs");
            if (ivsProp != null)
            {
                var raw = ivsProp.GetValue(e);
                // A fixed IV is only valid in range 0..31. Anything else
                // (-1, 252, 255, etc.) is a "random/unspecified" sentinel → null.
                static int? Fix(int v) => v is >= 0 and <= 31 ? v : null;

                if (raw is IndividualValueSet ivs && ivs.IsSpecified)
                {
                    fixedIVs[0] = Fix(ivs.HP);
                    fixedIVs[1] = Fix(ivs.ATK);
                    fixedIVs[2] = Fix(ivs.DEF);
                    fixedIVs[3] = Fix(ivs.SPA);
                    fixedIVs[4] = Fix(ivs.SPD);
                    fixedIVs[5] = Fix(ivs.SPE);
                }
                else if (raw is int[] ivsArr && ivsArr.Length >= 6)
                {
                    for (int i = 0; i < 6; i++)
                        fixedIVs[i] = Fix(ivsArr[i]);
                }
            }

            var flProp = e.GetType().GetProperty("FlawlessIVCount");
            if (flProp != null && flProp.GetValue(e) is byte fl)
                flawlessIVCount = fl;
        }

        // ── Moves ─────────────────────────────────────────────────────────────
        int[] fixedMoves = [0, 0, 0, 0];
        {
            var mProp = e.GetType().GetProperty("Moves");
            if (mProp != null && mProp.GetValue(e) is Moveset ms && ms.HasMoves)
            {
                fixedMoves[0] = ms.Move1;
                fixedMoves[1] = ms.Move2;
                fixedMoves[2] = ms.Move3;
                fixedMoves[3] = ms.Move4;
            }
        }

        // ── Ability ───────────────────────────────────────────────────────────
        string abilitySlot = e.Ability switch
        {
            AbilityPermission.OnlyFirst  => "Ability1",
            AbilityPermission.OnlySecond => "Ability2",
            AbilityPermission.OnlyHidden => "HiddenAbility",
            AbilityPermission.Any12      => "Any12",
            AbilityPermission.Any12H     => "Any12H",
            _                            => "Any12H",
        };

        // ── Legal balls for this specific encounter ────────────────────────────
        var legalBallIds = new List<int>();
        if (e is IEncounterTemplate template)
        {
            Span<Ball> buf = stackalloc Ball[30];
            int count = BallApplicator.GetLegalBalls(buf, pk, template);
            for (int i = 0; i < count; i++) legalBallIds.Add((int)buf[i]);
        }

        // ── Size ──────────────────────────────────────────────────────────────
        int? fixedSize = null;
        {
            var sProp = e.GetType().GetProperty("Size");
            if (sProp != null && sProp.GetValue(e) is byte sz)
                fixedSize = sz > 0 ? sz : null;
        }

        return new EncounterDetail(
            type, e.LevelMin, e.LevelMax, loc, e.IsEgg, shiny, isAlpha,
            fixedGender, fixedNature, fixedIVs, flawlessIVCount, fixedMoves,
            abilitySlot, legalBallIds, fixedSize
        );
    }

    public List<MoveEntry> GetMoves(string game, int species, int form = 0)
    {
        if (!GameMap.TryGetValue(game, out var version))
            throw new ArgumentException($"Unknown game: {game}");

        var learnSource = GameData.GetLearnSource(version);
        var learnset = learnSource.GetLearnset((ushort)species, (byte)form);
        var moveIds = learnset.GetAllMoves().ToArray();

        return moveIds
            .Where(id => id > 0 && id < _strings.Move.Count && !string.IsNullOrWhiteSpace(_strings.Move[id]))
            .Distinct()
            .Select(id => new MoveEntry(id, _strings.Move[id], GetMoveTypeName(id)))
            .OrderBy(m => m.Name)
            .ToList();
    }

    public List<AbilityInfo> GetAbilities(string game, int species, int form = 0)
    {
        if (!GameMap.TryGetValue(game, out var version))
            throw new ArgumentException($"Unknown game: {game}");

        var personal = GameData.GetPersonal(version);
        var pi = personal.GetFormEntry((ushort)species, (byte)form);
        var result = new List<AbilityInfo>();

        int count = pi.AbilityCount;
        var seen = new HashSet<int>();

        for (int i = 0; i < count && i < 3; i++)
        {
            int abilityId = pi.GetAbilityAtIndex(i);
            if (abilityId <= 0 || !seen.Add(abilityId)) continue;
            if (abilityId >= _strings.Ability.Count) continue;
            result.Add(new AbilityInfo(abilityId, _strings.Ability[abilityId], i == 2));
        }

        return result;
    }

    // Lowest level a species can legally exist when it has no direct encounter
    // (obtained only by evolving): the highest level requirement in its evolution
    // lineage. e.g. Gengar = 25 (Haunter's level-up), even though the final step is a trade.
    private static int EvolutionMinLevel(EntityContext context, ushort species, byte form)
    {
        try
        {
            var tree = EvolutionTree.GetEvolutionTree(context);
            var lineage = new HashSet<ushort>(tree.Reverse.GetPreEvolutions(species, form).Select(p => p.Species)) { species };
            int max = 1;
            foreach (var sp in lineage)
                foreach (var meth in tree.Forward.GetForward(sp, form).Span)
                    if (lineage.Contains(meth.Species) && meth.Level > max) max = meth.Level;
            return max;
        }
        catch { return 1; }
    }

    public PokemonMeta GetMeta(string game, int species, int form = 0)
    {
        if (!GameMap.TryGetValue(game, out var version))
            throw new ArgumentException($"Unknown game: {game}");
        ContextMap.TryGetValue(game, out var context);

        var pi = GameData.GetPersonal(version).GetFormEntry((ushort)species, (byte)form);

        var validGenders = pi.Gender switch
        {
            255 => new List<GenderOption> { new(2, "Genderless") },
            0   => new List<GenderOption> { new(0, "Male") },
            254 => new List<GenderOption> { new(1, "Female") },
            _   => new List<GenderOption> { new(0, "Male"), new(1, "Female") },
        };

        string statSystem = game switch
        {
            "LGLE" => "AV",
            "PLA"  => "EL",
            _      => "EV",
        };

        int statMax = statSystem switch
        {
            "AV" => 200,
            "EL" => 10,
            _    => 252,
        };

        int statTotal = statSystem == "EV" ? 510 : -1;

        bool hasTeraType = game is "SV";
        bool hasScale    = game is "SV" or "ZA" or "PLA";
        bool hasAlpha    = game is "ZA" or "PLA";

        // Compute encounter data once — used for minLevel and shiny legality
        var pk = CreateBlankPkm(version);
        pk.Species = (ushort)species;
        pk.Form = (byte)form;
        pk.Version = version;

        // Native encounters; HOME-transfer-only species fall back to the source
        // games so their shiny/level legality reflects how they actually arrive.
        var allEncounters = EncounterMovesetGenerator
            .GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, new GameVersion[] { version })
            .ToList();
        if (allEncounters.Count == 0)
        {
            allEncounters = EncounterMovesetGenerator
                .GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, TransferVersions)
                .ToList();
        }

        // Levels must be based on encounters of THIS exact species — not its
        // pre-evolutions. Otherwise an evolved Pokémon inherits its pre-evo's egg
        // (Lv1). e.g. Garchomp's own encounters start at 45, not Gible's egg.
        var selfEncounters = allEncounters.Where(e => e.Species == (ushort)species).ToList();
        var alphaEncounters = selfEncounters.Where(e => e is EncounterSlot9a { IsAlpha: true }).ToList();
        var normalSelf = selfEncounters.Where(e => e is not EncounterSlot9a { IsAlpha: true }).ToList();

        int minLevel;
        if (normalSelf.Count > 0)
            minLevel = normalSelf.Min(e => (int)e.LevelMin);
        else if (selfEncounters.Count > 0)
            minLevel = selfEncounters.Min(e => (int)e.LevelMin);
        else
            // Evolve-only here (no direct encounter): use the real evolution level.
            minLevel = EvolutionMinLevel(context, (ushort)species, (byte)form);

        int alphaMinLevel = alphaEncounters.Count > 0
            ? alphaEncounters.Min(e => (int)e.LevelMin) : 0;
        int alphaMaxLevel = alphaEncounters.Count > 0
            ? alphaEncounters.Max(e => (int)e.LevelMax) : 0;

        // canBeShiny = true only if at least one encounter in this game is not shiny-locked.
        // Count==0 means the species isn't obtainable here at all → not shiny.
        bool canBeShiny = allEncounters.Count > 0
            && allEncounters.Any(e => e.Shiny != Shiny.Never);

        return new PokemonMeta(validGenders, statSystem, statMax, statTotal, hasTeraType, canBeShiny, hasScale, hasAlpha, minLevel, 100, alphaMinLevel, alphaMaxLevel);
    }

    public List<NatureInfo> GetNatures()
    {
        var statNames = new[] { "—", "Atk", "Def", "SpA", "SpD", "Spe" };
        var result = new List<NatureInfo>();

        for (int i = 0; i < 25; i++)
        {
            int raised = i % 5;
            int lowered = i / 5;
            result.Add(new NatureInfo(
                i,
                _strings.Natures[i],
                raised == 0 ? "—" : statNames[raised + 1],
                lowered == 0 ? "—" : statNames[lowered + 1]
            ));
        }

        return result;
    }

    public List<ItemInfo> GetItems(string game)
    {
        if (!ContextMap.TryGetValue(game, out var context))
            throw new ArgumentException($"Unknown game: {game}");

        var result = new List<ItemInfo>();
        int maxItem = Math.Min(_strings.Item.Count, 2000);

        for (int id = 1; id < maxItem; id++)
        {
            var name = _strings.Item[id];
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!ItemRestrictions.IsHeldItemAllowed(id, context)) continue;

            // Exclude Mega Stones — they are game mechanics, not equippable held items for normal use
            if (IsMegaStone((ushort)id, context)) continue;

            result.Add(new ItemInfo(id, name));
        }

        return result.OrderBy(i => i.Name).ToList();
    }

    private static bool IsMegaStone(ushort itemId, EntityContext context) => context switch
    {
        EntityContext.Gen9a => ItemStorage9ZA.IsMegaStone(itemId),
        // Mega Stones in other games: item IDs 658–743 (Gen 6/7 range) — filter by name suffix
        _ => false,
    };

    public List<ItemInfo> GetBalls(string game, int species = 0, int form = 0)
    {
        if (!GameMap.TryGetValue(game, out var version))
            throw new ArgumentException($"Unknown game: {game}");

        var pk = CreateBlankPkm(version);
        pk.Species = (ushort)species;
        pk.Form = (byte)form;
        pk.Version = version;

        var versions = new GameVersion[] { version };
        var encounters = species > 0
            ? EncounterMovesetGenerator
                .GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, versions)
                .ToList()
            : [];

        var legalBallIds = new HashSet<int>();
        Span<Ball> buffer = stackalloc Ball[30];

        if (game == "PLA")
        {
            // PLA has its own ball system — always include all native catch balls
            foreach (int id in new[] { 1710, 1711, 1712, 1713, 1746, 1747, 1748, 1749, 1750 })
                legalBallIds.Add(id);

            // Add event Cherish Ball when there are gift/event encounters
            bool hasEvent = encounters.Any(e => e.GetType().Name.Contains("Gift8a") || e.GetType().Name.Contains("WC8"));
            if (hasEvent) legalBallIds.Add((int)Ball.Cherish);

            // Origin Ball for Dialga/Palkia in their origin forms
            if ((species == 483 || species == 484) && form == 1)
                legalBallIds.Add(1771);
        }
        else
        {
            foreach (var enc in encounters)
            {
                if (enc is not IEncounterTemplate template) continue;
                int count = BallApplicator.GetLegalBalls(buffer, pk, template);
                for (int i = 0; i < count; i++)
                    legalBallIds.Add((int)buffer[i]);
            }

            // Fallback when no encounter data or BallApplicator returns nothing
            if (legalBallIds.Count == 0)
            {
                int count = BallApplicator.GetLegalBalls(buffer, pk);
                for (int i = 0; i < count; i++)
                    legalBallIds.Add((int)buffer[i]);
            }
        }

        // Use game-specific item strings so PLA ball names (1710+) resolve correctly
        var itemNames = game == "PLA"
            ? _strings.GetItemStrings(EntityContext.Gen8a, GameVersion.PLA)
            : (IReadOnlyList<string>)_strings.Item;

        return legalBallIds
            .Where(id => id > 0 && id < itemNames.Count)
            .Select(id => new ItemInfo(id, itemNames[id]))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Name.Contains("Ball", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name)
            .ToList();
    }

    public string ToShowdown(PokemonConfig config)
    {
        var pk = BuildPkm(config);

        // Strip PKHeX's own Friendship line — we always emit it in the correct position ourselves
        var rawText = ShowdownParsing.GetShowdownText(pk);
        var baseLines = rawText.Split('\n').Where(l => !l.StartsWith("Friendship:")).ToList();

        var extra = new List<string>();

        // Scale (dot-prefix PKHeX format) — only SV, ZA, PLA
        if (config.Game is "SV" or "ZA" or "PLA")
            extra.Add($".Scale={config.Scale}");

        // Met date in YYYYMMDD format (no dashes)
        if (!string.IsNullOrWhiteSpace(config.MetDate)
            && DateOnly.TryParse(config.MetDate, out var d))
            extra.Add($".MetDate={d:yyyyMMdd}");

        // Standard Showdown friendship field
        extra.Add($"Friendship: {config.Friendship}");

        // PKHeX dot-prefix OT friendship
        extra.Add($".OriginalTrainerFriendship={config.Friendship}");

        // Trainer info — only when the user opts into a custom trainer.
        // Otherwise AutoOT applies the receiving trainer's OT/TID/SID on trade.
        if (config.UseCustomOT)
        {
            if (!string.IsNullOrWhiteSpace(config.OT))
                extra.Add($"OT: {config.OT}");
            extra.Add($"TID: {config.TID}");
            extra.Add($"SID: {config.SID}");
            if (!string.IsNullOrWhiteSpace(config.Language) && config.Language != "English")
                extra.Add($"Language: {config.Language}");
        }

        return ".trade " + string.Join("\n", baseLines).TrimEnd() + "\n" + string.Join("\n", extra);
    }

    public ValidationResult Validate(PokemonConfig config)
    {
        var pk = BuildPkm(config);
        var la = new LegalityAnalysis(pk);

        bool isLegal = la.Results.All(r => r.Valid);
        string report = la.ToString() ?? "";

        var issues = la.Results
            .Where(r => !r.Valid)
            .Select(r => new LegalityIssue(
                r.Identifier.ToString(),
                r.Result.ToString(),
                r.Judgement.ToString()
            ))
            .ToList();

        return new ValidationResult(isLegal, report, issues);
    }

    public (byte[] Data, string FileName) Generate(PokemonConfig config)
    {
        var pk = BuildPkm(config);
        pk.RefreshChecksum();

        var ext = pk switch
        {
            PK9 => ".pk9",
            PA9 => ".pa9",
            PK8 => ".pk8",
            PA8 => ".pa8",
            PB8 => ".pb8",
            PB7 => ".pb7",
            _ => ".pkm"
        };

        var name = string.IsNullOrWhiteSpace(config.Nickname)
            ? _strings.Species[config.Species]
            : config.Nickname;

        return (pk.Data.ToArray(), $"{name}{ext}");
    }

    private PKM BuildPkm(PokemonConfig config)
    {
        if (!GameMap.TryGetValue(config.Game, out var version))
            throw new ArgumentException($"Unknown game: {config.Game}");

        var pk = CreateBlankPkm(version);
        var personal = GameData.GetPersonal(version);
        var pi = personal.GetFormEntry((ushort)config.Species, (byte)config.Form);

        pk.Species = (ushort)config.Species;
        pk.Form = (byte)config.Form;
        pk.Version = version;
        pk.EXP = Experience.GetEXP((byte)config.Level, pi.EXPGrowth);

        pk.Nature = (Nature)config.Nature;
        pk.StatNature = (Nature)config.Nature;
        pk.Gender = (byte)config.Gender;
        pk.CurrentFriendship = (byte)config.Friendship;

        pk.Nickname = !string.IsNullOrWhiteSpace(config.Nickname)
            ? config.Nickname
            : _strings.Species[config.Species];
        pk.IsNicknamed = !string.IsNullOrWhiteSpace(config.Nickname);

        pk.OriginalTrainerName = config.OT;
        pk.TID16 = (ushort)config.TID;
        pk.SID16 = (ushort)config.SID;

        pk.Language = config.Language switch
        {
            "Japanese" => 1,
            "English"  => 2,
            "French"   => 3,
            "Italian"  => 4,
            "German"   => 5,
            "Spanish"  => 7,
            "Korean"   => 8,
            _          => 2,
        };

        pk.HeldItem = config.HeldItem;
        pk.Ball = (byte)config.Ball;

        pk.Move1 = config.Moves.Length > 0 ? (ushort)config.Moves[0] : (ushort)0;
        pk.Move2 = config.Moves.Length > 1 ? (ushort)config.Moves[1] : (ushort)0;
        pk.Move3 = config.Moves.Length > 2 ? (ushort)config.Moves[2] : (ushort)0;
        pk.Move4 = config.Moves.Length > 3 ? (ushort)config.Moves[3] : (ushort)0;
        pk.HealPP();

        if (config.EVs.Length >= 6)
        {
            pk.EV_HP  = config.EVs[0];
            pk.EV_ATK = config.EVs[1];
            pk.EV_DEF = config.EVs[2];
            pk.EV_SPA = config.EVs[3];
            pk.EV_SPD = config.EVs[4];
            pk.EV_SPE = config.EVs[5];
        }

        if (config.IVs.Length >= 6)
        {
            ReadOnlySpan<int> ivs = [config.IVs[0], config.IVs[1], config.IVs[2],
                                     config.IVs[3], config.IVs[4], config.IVs[5]];
            pk.SetIVs(ivs);
        }

        if (config.IsShiny)
            CommonEdits.SetIsShiny(pk, true);
        else
            CommonEdits.SetUnshiny(pk);

        if (config.Ability > 0)
            CommonEdits.SetAbility(pk, config.Ability);

        if (pk is PK9 pk9)
        {
            pk9.TeraTypeOriginal = (MoveType)config.TeraType;
            pk9.Scale = (byte)Math.Clamp(config.Scale, 0, 255);
        }
        if (pk is PA9 pa9)
        {
            pa9.Scale = (byte)Math.Clamp(config.Scale, 0, 255);
            pa9.IsAlpha = config.IsAlpha;
            if (config.IsAlpha)
                pa9.RibbonMarkAlpha = true;
        }
        if (pk is PA8 pa8)
        {
            pa8.Scale = (byte)Math.Clamp(config.Scale, 0, 255);
            pa8.IsAlpha = config.IsAlpha;
            if (config.IsAlpha)
                pa8.RibbonMarkAlpha = true;
        }

        // Met date
        if (!string.IsNullOrWhiteSpace(config.MetDate)
            && DateOnly.TryParse(config.MetDate, out var metDate))
            pk.MetDate = metDate;
        else
            pk.MetDate = DateOnly.FromDateTime(DateTime.Now);

        return pk;
    }

    private static PKM CreateBlankPkm(GameVersion version) => version switch
    {
        GameVersion.SL or GameVersion.VL => new PK9(),
        GameVersion.ZA                   => new PA9(),
        GameVersion.SW or GameVersion.SH => new PK8(),
        GameVersion.PLA                  => new PA8(),
        GameVersion.BD or GameVersion.SP => new PB8(),
        GameVersion.GP or GameVersion.GE => new PB7(),
        _ => new PK9()
    };

    private string GetMoveTypeName(int moveId)
    {
        if (moveId <= 0 || moveId >= _strings.Move.Count) return "Normal";
        return "Normal";
    }
}
