namespace PokecreatorBot.Data;

public record GameEntry(string Id, string Name);

public record SpeciesInfo(int Id, string Name, bool Native = true, int Form = 0, string? FormName = null);

public record NatureInfo(int Id, string Name, string RaisedStat, string LoweredStat);

public record MoveEntry(int Id, string Name, string Type);

public record AbilityInfo(int Id, string Name, bool IsHidden);

public record ItemInfo(int Id, string Name);

public record EncounterDetail(
    string Type,           // Wild / Static / Gift / Trade / Egg / Raid / Event / Outbreak
    int LevelMin,
    int LevelMax,
    string? Location,
    bool IsEgg,
    string Shiny,          // Never | Random | Always | AlwaysStar | AlwaysSquare
    bool IsAlpha,
    int? FixedGender,      // null = species default; 0 = Male; 1 = Female; 2 = Genderless
    int? FixedNature,      // null = any; 0-24 = locked nature
    int?[] FixedIVs,       // 6 elements [HP ATK DEF SPA SPD SPE]; null = any; 0-31 = locked
    int FlawlessIVCount,   // number of guaranteed 31 IVs (unspecified which stats)
    int[] FixedMoves,      // 4 move IDs; 0 = not fixed by this encounter
    string AbilitySlot,    // Any12 | Any12H | Ability1 | Ability2 | HiddenAbility
    List<int> LegalBallIds,
    int? FixedSize         // null = user choice; 0-255 = locked (SV/ZA/PLA only)
);

public record PokemonConfig
{
    public string Game { get; init; } = "SV";
    public int Species { get; init; }
    public int Form { get; init; }
    public int Level { get; init; } = 50;
    public bool IsShiny { get; init; }
    public int Gender { get; init; } = 0;
    public int Nature { get; init; }
    public int Ability { get; init; }
    public int HeldItem { get; init; }
    public int Ball { get; init; } = 4;
    public int[] Moves { get; init; } = new int[4];
    public int[] EVs { get; init; } = new int[6];
    public int[] IVs { get; init; } = [31, 31, 31, 31, 31, 31];
    public bool EVsSet { get; init; } = false;
    public bool IVsSet { get; init; } = false;
    public int Friendship { get; init; } = 255;
    public bool FriendshipSet { get; init; } = false;
    public int Scale { get; init; } = 128;
    public bool ScaleSet { get; init; } = false;
    public bool IsAlpha { get; init; } = false;
    public string MetDate { get; init; } = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
    public bool MetDateSet { get; init; } = false;
    public int DynamaxLevel { get; init; } = 0;
    public bool DynamaxSet { get; init; } = false;
    public bool UseCustomOT { get; init; } = false;
    public string OT { get; init; } = "Trainer";
    public int TID { get; init; }
    public int SID { get; init; }
    public string Language { get; init; } = "English";
    public string? Nickname { get; init; }
    public int TeraType { get; init; } = 18;
}

public record ValidationResult(bool IsLegal, string Report, List<LegalityIssue> Issues);

public record LegalityIssue(string Field, string Message, string Severity);

// Result of an AutoLegality Mod generation pass. Ok == false means the requested
// combination cannot legally exist (Report explains why).
public record LegalGenResult(
    bool Ok,
    string TradeText,
    byte[]? File,
    string? FileName,
    string Status,
    string? Report);

public record GenderOption(int Value, string Name);

public record PokemonMeta(
    List<GenderOption> ValidGenders,
    string StatSystem,   // "EV" | "AV" | "EL"
    int StatMax,         // 252 | 200 | 10
    int StatTotal,       // 510 | -1 (no cap) | -1
    bool HasTeraType,
    bool CanBeShiny,
    bool HasScale,       // size slider (SV, ZA, PLA)
    bool HasAlpha,       // alpha toggle (ZA, PLA)
    int MinLevel,        // lowest legal non-alpha encounter level
    int MaxLevel,        // always 100 (can train any Pokemon)
    int AlphaMinLevel,   // lowest alpha encounter level (0 if no alpha encounters)
    int AlphaMaxLevel,   // highest alpha encounter level (0 if no alpha encounters)
    int ShinyMinLevel    // lowest level a SHINY can legally be (event-shiny mons differ, e.g. shiny Koraidon = 100)
);
