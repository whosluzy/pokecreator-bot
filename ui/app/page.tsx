"use client";
import { useEffect, useState, useCallback } from "react";
import {
  api,
  GameEntry, SpeciesInfo, NatureInfo, MoveEntry,
  AbilityInfo, ItemInfo, EncounterDetail, PokemonMeta, PokemonConfig,
} from "@/lib/api";

const STAT_NAMES = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
const GENDER_NAMES = ["Male", "Female", "Genderless"];
// Index must match PKHeX MoveType enum (used for TeraTypeOriginal)
const TERA_TYPES = [
  "Normal","Fighting","Flying","Poison","Ground","Rock","Bug","Ghost","Steel",
  "Fire","Water","Grass","Electric","Psychic","Ice","Dragon","Dark","Fairy","Stellar",
];
const LANGUAGES = ["English","Japanese","French","Italian","German","Spanish","Korean"];

const DEFAULT_META: PokemonMeta = {
  validGenders: [{ value: 0, name: "Male" }, { value: 1, name: "Female" }],
  statSystem: "EV", statMax: 252, statTotal: 510, hasTeraType: true, canBeShiny: true,
  hasScale: true, hasAlpha: false, minLevel: 1, maxLevel: 100, alphaMinLevel: 0, alphaMaxLevel: 0,
};

const TODAY = new Date().toISOString().slice(0, 10);

const DEFAULT_CONFIG: PokemonConfig = {
  game: "SV", species: 1, form: 0, level: 5, isShiny: false,
  gender: 0, nature: 0, ability: 0, heldItem: 0, ball: 4,
  moves: [0, 0, 0, 0], eVs: [0, 0, 0, 0, 0, 0], iVs: [31, 31, 31, 31, 31, 31],
  friendship: 255, scale: 128, isAlpha: false, metDate: TODAY,
  useCustomOT: false, oT: "Trainer", tID: 0, sID: 0,
  language: "English", nickname: "", teraType: 0,
};

function sizeLabel(scale: number) {
  return scale < 100 ? "XS" : scale < 128 ? "S" : scale === 128 ? "M" : scale < 160 ? "L" : "XL";
}

export default function Home() {
  const [games, setGames] = useState<GameEntry[]>([]);
  const [species, setSpecies] = useState<SpeciesInfo[]>([]);
  const [natures, setNatures] = useState<NatureInfo[]>([]);
  const [moves, setMoves] = useState<MoveEntry[]>([]);
  const [abilities, setAbilities] = useState<AbilityInfo[]>([]);
  const [items, setItems] = useState<ItemInfo[]>([]);
  const [balls, setBalls] = useState<ItemInfo[]>([]);
  const [encounters, setEncounters] = useState<EncounterDetail[]>([]);
  const [filterShiny, setFilterShiny] = useState(false);
  const [filterAlpha, setFilterAlpha] = useState(false);
  const [meta, setMeta] = useState<PokemonMeta>(DEFAULT_META);
  const [showdown, setShowdown] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [config, setConfig] = useState<PokemonConfig>(DEFAULT_CONFIG);
  const [generating, setGenerating] = useState(false);
  const [speciesSearch, setSpeciesSearch] = useState("");

  const set = useCallback(<K extends keyof PokemonConfig>(key: K, val: PokemonConfig[K]) => {
    setConfig(c => ({ ...c, [key]: val }));
    setShowdown(null);
  }, []);

  useEffect(() => {
    api.getGames().then(setGames);
    api.getNatures().then(setNatures);
  }, []);

  // Game change → species list + items
  useEffect(() => {
    if (!config.game) return;
    api.getSpecies(config.game).then(s => {
      setSpecies(s);
      if (s.length > 0) setConfig(c => ({ ...c, species: s[0].id, form: s[0].form }));
    });
    api.getItems(config.game).then(setItems);
  }, [config.game]);

  // Species/form change → meta, moves, abilities, encounters, ball name lookup
  useEffect(() => {
    if (!config.game || !config.species) return;
    api.getMeta(config.game, config.species, config.form).then(setMeta);
    api.getMoves(config.game, config.species, config.form).then(setMoves);
    api.getAbilities(config.game, config.species, config.form).then(setAbilities);
    api.getBalls(config.game, config.species, config.form).then(setBalls);
    api.getEncounters(config.game, config.species, config.form).then(e => {
      setEncounters(e);
      setFilterShiny(false);
      setFilterAlpha(false);
    });
  }, [config.game, config.species, config.form]);

  // Filter encounters by the user's shiny/alpha intent — only legal ones remain
  const filteredEncounters = encounters.filter(e => {
    if (filterShiny && e.shiny === "Never") return false;          // can't be shiny here
    if (!filterShiny && e.shiny.startsWith("Always")) return false; // always-shiny encounter
    if (meta.hasAlpha && filterAlpha !== e.isAlpha) return false;   // alpha mismatch
    return true;
  });

  const filteredSpecies = species.filter(s => {
    const q = speciesSearch.toLowerCase();
    return s.name.toLowerCase().includes(q)
      || (s.formName?.toLowerCase().includes(q) ?? false);
  });

  // ── Aggregate the legal possibilities across ALL matching encounters ──────
  // (no encounter picking — the union of what every matching encounter allows)
  const agg = (() => {
    const ballIds = new Set<number>();
    const natureSet = new Set<number>();
    const genderSet = new Set<number>();
    let anyFreeNature = false, anyFreeGender = false;
    let allowAny12H = false, allowAny12 = false;
    const fixedSlots = new Set<string>();
    let minLevel = 100;

    for (const e of filteredEncounters) {
      e.legalBallIds.forEach(b => ballIds.add(b));
      minLevel = Math.min(minLevel, e.levelMin);
      if (e.fixedNature == null) anyFreeNature = true; else natureSet.add(e.fixedNature);
      if (e.fixedGender == null) anyFreeGender = true; else genderSet.add(e.fixedGender);
      if (e.abilitySlot === "Any12H") allowAny12H = true;
      else if (e.abilitySlot === "Any12") allowAny12 = true;
      else fixedSlots.add(e.abilitySlot);
    }

    // Abilities
    const nonHidden = abilities.filter(a => !a.isHidden);
    const hidden = abilities.filter(a => a.isHidden);
    const abilityIds = new Set<number>();
    if (allowAny12H) abilities.forEach(a => abilityIds.add(a.id));
    else {
      if (allowAny12) nonHidden.forEach(a => abilityIds.add(a.id));
      if (fixedSlots.has("Ability1") && nonHidden[0]) abilityIds.add(nonHidden[0].id);
      if (fixedSlots.has("Ability2") && nonHidden[1]) abilityIds.add(nonHidden[1].id);
      if (fixedSlots.has("HiddenAbility")) hidden.forEach(a => abilityIds.add(a.id));
    }

    const has = filteredEncounters.length > 0;
    return {
      hasEncounters: has,
      minLevel: has ? minLevel : (meta.minLevel > 0 ? meta.minLevel : 1),
      ballOptions: has ? balls.filter(b => ballIds.has(b.id)) : balls,
      natureOptions: !has || anyFreeNature ? natures : natures.filter(n => natureSet.has(n.id)),
      genderOptions: !has || anyFreeGender ? meta.validGenders : meta.validGenders.filter(g => genderSet.has(g.value)),
      abilityOptions: !has || abilityIds.size === 0 ? abilities : abilities.filter(a => abilityIds.has(a.id)),
    };
  })();

  const lvMin = agg.minLevel;
  const natureOptions = agg.natureOptions;
  const genderOptions = agg.genderOptions.length ? agg.genderOptions : meta.validGenders;
  const ballOptions = agg.ballOptions;
  const abilityOptions = agg.abilityOptions;

  // A field is effectively "locked" when only one legal value remains
  const natureLocked  = agg.hasEncounters && natureOptions.length === 1;
  const genderLocked  = genderOptions.length === 1;
  const abilityLocked = abilityOptions.length === 1;

  // Reconcile current selections so they stay within the legal sets
  useEffect(() => {
    setConfig(c => {
      const n = { ...c };
      n.isShiny = meta.canBeShiny ? filterShiny : false;
      n.isAlpha = meta.hasAlpha ? filterAlpha : false;
      if (c.level < lvMin) n.level = lvMin;
      if (genderOptions.length && !genderOptions.find(g => g.value === c.gender)) n.gender = genderOptions[0].value;
      if (natureOptions.length && !natureOptions.find(x => x.id === c.nature)) n.nature = natureOptions[0].id;
      if (abilityOptions.length && !abilityOptions.find(a => a.id === c.ability)) n.ability = abilityOptions[0].id;
      if (ballOptions.length && !ballOptions.find(b => b.id === c.ball)) n.ball = ballOptions[0].id;
      return n;
    });
    setShowdown(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterShiny, filterAlpha, encounters, abilities, balls, meta]);

  const totalStat = config.eVs.reduce((a, b) => a + b, 0);
  const statLabel = meta.statSystem === "AV" ? "AV" : meta.statSystem === "EL" ? "EL" : "EV";
  const statCap = meta.statMax;
  const totalCap = meta.statTotal;

  const setStat = (i: number, raw: number) => {
    const vals = [...config.eVs];
    const clamped = Math.max(0, Math.min(statCap, raw));
    if (totalCap > 0) {
      const others = vals.reduce((s, v, j) => j === i ? s : s + v, 0);
      vals[i] = Math.min(clamped, totalCap - others);
    } else {
      vals[i] = clamped;
    }
    set("eVs", vals as number[]);
  };

  // Live Showdown export (debounced)
  useEffect(() => {
    const t = setTimeout(async () => {
      try {
        const res = await fetch("/api/pokemon/showdown", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(config),
        });
        const data = await res.json();
        setShowdown(data.text ?? "");
      } catch { /* silent */ }
    }, 400);
    return () => clearTimeout(t);
  }, [config]);

  const handleCopy = () => {
    if (!showdown) return;
    navigator.clipboard.writeText(showdown).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  };

  const handleGenerate = async () => {
    setGenerating(true);
    try { await api.generate(config); }
    catch (e) { console.error(e); }
    finally { setGenerating(false); }
  };

  const lockTag = (text: string) => (
    <span style={{ marginLeft: 6, fontSize: "0.7rem", color: "#fbbf24" }}>🔒 {text}</span>
  );

  return (
    <div className="container">
      <h1>PokeCreator</h1>
      <p style={{ color: "#6b7280", marginBottom: "24px" }}>
        Build legal Pokémon using PKHeX data — pick an encounter and every option locks to what&apos;s legal.
      </p>

      {/* Game + Species */}
      <div className="card">
        <h2>Game &amp; Pokémon</h2>
        <div className="grid-2" style={{ marginBottom: 12 }}>
          <div>
            <label>Game</label>
            <select value={config.game} onChange={e => set("game", e.target.value)}>
              {games.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </div>
          <div>
            <label>Search</label>
            <input type="text" placeholder="e.g. Pikachu" value={speciesSearch}
              onChange={e => setSpeciesSearch(e.target.value)} />
          </div>
        </div>
        <label>Pokémon ({filteredSpecies.length} available)</label>
        <div style={{
          height: 160, overflowY: "auto", background: "#0f0f1a",
          border: "1px solid #2d2d4e", borderRadius: 8,
        }}>
          {filteredSpecies.map(s => {
            const selected = config.species === s.id && config.form === s.form;
            return (
              <div
                key={`${s.id}-${s.form}`}
                onClick={() => setConfig(c => ({ ...c, species: s.id, form: s.form }))}
                style={{
                  padding: "5px 10px", cursor: "pointer", fontSize: "0.9rem",
                  background: selected ? "#2d1b69" : "transparent",
                  color: selected ? "#c4b5fd" : "#e8e8f0",
                  borderLeft: selected ? "3px solid #7c3aed" : "3px solid transparent",
                  userSelect: "none",
                }}
              >
                #{s.id} {s.name}
                {s.formName && (
                  <span style={{ marginLeft: 6, fontSize: "0.78rem", color: "#fbbf24" }}>
                    ({s.formName})
                  </span>
                )}
                {!s.native && (
                  <span style={{ marginLeft: 6, fontSize: "0.7rem", color: "#60a5fa" }}
                    title="Not catchable here — must be transferred in via Pokémon HOME">
                    ⇄ HOME
                  </span>
                )}
              </div>
            );
          })}
        </div>
        <p style={{ color: "#6b7280", fontSize: "0.72rem", marginTop: 6 }}>
          <span style={{ color: "#60a5fa" }}>⇄ HOME</span> = legal in this game only via Pokémon HOME transfer.
        </p>
      </div>

      {/* Shiny / Alpha intent → filters which encounters are legal */}
      <div className="card">
        <h2>Shiny &amp; Alpha</h2>
        <p style={{ color: "#6b7280", fontSize: "0.8rem", marginBottom: 10 }}>
          Pick what you want first — the encounter list below shows only the options that can legally produce it.
        </p>
        <div style={{ display: "flex", gap: 24, alignItems: "center" }}>
          {meta.canBeShiny ? (
            <label className="shiny-toggle">
              <input type="checkbox" checked={filterShiny}
                onChange={e => setFilterShiny(e.target.checked)} />
              ✨ Shiny
            </label>
          ) : (
            <span style={{ fontSize: "0.8rem", color: "#6b7280" }}>✨ Shiny — not obtainable in this game</span>
          )}
          {meta.hasAlpha ? (
            <label className="shiny-toggle">
              <input type="checkbox" checked={filterAlpha}
                onChange={e => setFilterAlpha(e.target.checked)} />
              α Alpha
            </label>
          ) : (
            <span style={{ fontSize: "0.8rem", color: "#6b7280" }}>α Alpha — N/A for this game</span>
          )}
        </div>
      </div>

      {/* Illegal-combo warning */}
      {encounters.length > 0 && filteredEncounters.length === 0 && (
        <div className="card" style={{ borderColor: "#7f1d1d", background: "#1f0f0f" }}>
          <span style={{ color: "#f87171", fontSize: "0.9rem" }}>
            ⚠ No {filterShiny ? "shiny " : ""}{filterAlpha ? "alpha " : ""}
            version of this Pokémon is legally obtainable in this game. The options below may not be legal.
          </span>
        </div>
      )}

      {/* Basic Info */}
      <div className="card">
        <h2>Basic Info</h2>

        {/* Current shiny/alpha status (set above via Shiny & Alpha) */}
        <div style={{ display: "flex", gap: 16, marginBottom: 14, fontSize: "0.82rem", color: "#9ca3af" }}>
          <span>{config.isShiny ? "✨ Shiny" : "Not shiny"}</span>
          {meta.hasAlpha && <span>{config.isAlpha ? "α Alpha" : "Not alpha"}</span>}
        </div>

        {/* Level → Nature → Ability */}
        <div className="grid-3" style={{ marginBottom: 12 }}>
          <div>
            <label>Level ({lvMin}–100)</label>
            <input type="number" min={lvMin} max={100}
              value={config.level}
              onChange={e => set("level", Math.max(lvMin, Math.min(100, Number(e.target.value))))} />
          </div>
          <div>
            <label>Nature {natureLocked && lockTag("fixed")}</label>
            <select value={config.nature} disabled={natureLocked}
              onChange={e => set("nature", Number(e.target.value))}
              style={natureLocked ? { opacity: 0.7 } : undefined}>
              {natures.map(n => (
                <option key={n.id} value={n.id}>
                  {n.name} {n.raisedStat !== "—" ? `(+${n.raisedStat} -${n.loweredStat})` : "(Neutral)"}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label>Ability {abilityLocked && lockTag("fixed")}</label>
            <select value={config.ability} disabled={abilityLocked}
              onChange={e => set("ability", Number(e.target.value))}
              style={abilityLocked ? { opacity: 0.7 } : undefined}>
              {abilityOptions.map(a => (
                <option key={a.id} value={a.id}>{a.name}{a.isHidden ? " (HA)" : ""}</option>
              ))}
            </select>
          </div>
        </div>

        {/* Gender → Ball → Held Item → Tera */}
        <div className="grid-4">
          <div>
            <label>Gender {genderLocked && lockTag("fixed")}</label>
            <select value={config.gender} disabled={genderLocked}
              onChange={e => set("gender", Number(e.target.value))}
              style={genderLocked ? { opacity: 0.7 } : undefined}>
              {genderOptions.map(g => <option key={g.value} value={g.value}>{g.name}</option>)}
            </select>
          </div>
          <div>
            <label>Ball ({ballOptions.length})</label>
            <select value={config.ball} onChange={e => set("ball", Number(e.target.value))}>
              {ballOptions.map(b => <option key={b.id} value={b.id}>{b.name}</option>)}
            </select>
          </div>
          <div>
            <label>Held Item</label>
            <select value={config.heldItem} onChange={e => set("heldItem", Number(e.target.value))}>
              <option value={0}>None</option>
              {items.map(i => <option key={i.id} value={i.id}>{i.name}</option>)}
            </select>
          </div>
          {meta.hasTeraType && (
            <div>
              <label>Tera Type</label>
              <select value={config.teraType} onChange={e => set("teraType", Number(e.target.value))}>
                {TERA_TYPES.map((t, i) => <option key={i} value={i}>{t}</option>)}
              </select>
            </div>
          )}
        </div>
      </div>

      {/* Moves */}
      <div className="card">
        <h2>Moves</h2>
        {[0, 1, 2, 3].map(i => (
          <div key={i} className="move-slot">
            <span className="move-num">{i + 1}</span>
            <select value={config.moves[i]}
              onChange={e => {
                const newMoves = [...config.moves];
                newMoves[i] = Number(e.target.value);
                set("moves", newMoves as number[]);
              }}>
              <option value={0}>— None —</option>
              {moves.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
            </select>
          </div>
        ))}
      </div>

      {/* Stats */}
      <div className="card">
        <div className="section-header">
          <h2>
            {statLabel}s &amp; IVs
            <span style={{ fontSize: "0.75rem", fontWeight: 400, color: "#6b7280", marginLeft: 8 }}>
              {statLabel === "EV" ? "max 252 / stat · 510 total" :
               statLabel === "AV" ? "max 200 / stat" :
               "max 10 / stat (Effort Levels)"}
            </span>
          </h2>
          {totalCap > 0 && (
            <span style={{ fontSize: "0.85rem", color: totalStat > totalCap ? "#f87171" : "#6b7280" }}>
              {totalStat}/{totalCap}
            </span>
          )}
        </div>

        <div style={{ display: "grid", gridTemplateColumns: "44px 1fr 48px 16px 1fr 28px", gap: "0 8px", marginBottom: 4 }}>
          <span />
          <span style={{ fontSize: "0.7rem", color: "#6b7280", textAlign: "center" }}>{statLabel} (0–{statCap})</span>
          <span />
          <span />
          <span style={{ fontSize: "0.7rem", color: "#6b7280", textAlign: "center" }}>IV (0–31)</span>
          <span />
        </div>

        {STAT_NAMES.map((name, i) => {
          const evVal = config.eVs[i];
          const ivVal = config.iVs[i];
          const ivLocked = false;
          return (
            <div key={i} style={{ display: "grid", gridTemplateColumns: "44px 1fr 48px 16px 1fr 28px", gap: "4px 8px", alignItems: "center", marginBottom: 4 }}>
              <span style={{ fontSize: "0.8rem", color: "#9ca3af", textAlign: "right" }}>{name}</span>

              <input type="range" min={0} max={statCap} value={evVal}
                onChange={e => setStat(i, Number(e.target.value))}
                style={{ accentColor: "#7c3aed" }} />

              <input type="number" min={0} max={statCap} value={evVal}
                onChange={e => setStat(i, Number(e.target.value))}
                style={{ width: "100%", fontSize: "0.8rem", textAlign: "center" }} />

              <span style={{ color: "#374151", textAlign: "center", fontSize: "0.7rem" }}>│</span>

              <input type="range" min={0} max={31} value={ivVal} disabled={ivLocked}
                onChange={e => { const ivs = [...config.iVs]; ivs[i] = Number(e.target.value); set("iVs", ivs as number[]); }}
                style={{ accentColor: ivLocked ? "#fbbf24" : (ivVal === 31 ? "#4ade80" : "#34d399"), opacity: ivLocked ? 0.6 : 1 }} />

              <span style={{ fontSize: "0.8rem", color: ivLocked ? "#fbbf24" : (ivVal === 31 ? "#4ade80" : "#9ca3af"), textAlign: "center" }}>
                {ivVal}{ivLocked ? "🔒" : ""}
              </span>
            </div>
          );
        })}
      </div>

      {/* Trainer Info */}
      <div className="card">
        <h2>Trainer Info</h2>

        {/* AutoOT by default; custom trainer hidden behind a toggle */}
        <button
          className={config.useCustomOT ? "btn btn-primary" : "btn btn-secondary"}
          onClick={() => set("useCustomOT", !config.useCustomOT)}
          style={{ marginBottom: 10 }}>
          {config.useCustomOT ? "✓ Custom Trainer Name" : "Custom Trainer Name (not AutoOT)"}
        </button>

        {config.useCustomOT ? (
          <div className="grid-3" style={{ marginBottom: 12 }}>
            <div>
              <label>OT Name</label>
              <input type="text" value={config.oT} onChange={e => set("oT", e.target.value)} maxLength={12} />
            </div>
            <div>
              <label>TID</label>
              <input type="number" min={0} max={65535} value={config.tID}
                onChange={e => set("tID", Number(e.target.value))} />
            </div>
            <div>
              <label>SID</label>
              <input type="number" min={0} max={65535} value={config.sID}
                onChange={e => set("sID", Number(e.target.value))} />
            </div>
          </div>
        ) : (
          <p style={{ color: "#6b7280", fontSize: "0.8rem", marginBottom: 12 }}>
            Using <strong style={{ color: "#9ca3af" }}>AutoOT</strong> — the receiving trainer&apos;s name, TID &amp; SID are applied on trade.
          </p>
        )}

        <div className="grid-3" style={{ marginTop: 4 }}>
          <div>
            <label>Language</label>
            <select value={config.language} onChange={e => set("language", e.target.value)}>
              {LANGUAGES.map(l => <option key={l}>{l}</option>)}
            </select>
          </div>
          <div>
            <label>Nickname (blank = species name)</label>
            <input type="text" value={config.nickname}
              onChange={e => set("nickname", e.target.value)} maxLength={12} />
          </div>
          <div>
            <label>Friendship (0–255)</label>
            <input type="number" min={0} max={255} value={config.friendship}
              onChange={e => set("friendship", Number(e.target.value))} />
          </div>
        </div>

        <div className="grid-2" style={{ marginTop: 12 }}>
          <div>
            <label>Met Date</label>
            <input type="date" value={config.metDate} max={TODAY}
              onChange={e => set("metDate", e.target.value)}
              style={{ colorScheme: "dark" }} />
          </div>

          {meta.hasScale && (
            <div>
              <label>
                Size / Scale
                <span style={{ color: "#6b7280", marginLeft: 8, fontSize: "0.75rem" }}>
                  {sizeLabel(config.scale)} ({config.scale})
                </span>
              </label>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <span style={{ fontSize: "0.75rem", color: "#6b7280" }}>0</span>
                <input type="range" min={0} max={255} value={config.scale}
                  onChange={e => set("scale", Number(e.target.value))}
                  style={{ flex: 1, accentColor: "#7c3aed" }} />
                <span style={{ fontSize: "0.75rem", color: "#6b7280" }}>255</span>
              </div>
            </div>
          )}
        </div>
      </div>

      {/* Showdown preview */}
      <div className="card">
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 10 }}>
          <h2 style={{ margin: 0 }}>Showdown Export</h2>
          <div style={{ display: "flex", gap: 10 }}>
            <button className="btn btn-primary" onClick={handleCopy} style={{ minWidth: 110 }}>
              {copied ? "✓ Copied!" : "Copy Text"}
            </button>
            <button className="btn btn-success" onClick={handleGenerate} disabled={generating} style={{ minWidth: 130 }}>
              {generating ? "Generating..." : "Download .pk file"}
            </button>
          </div>
        </div>
        <textarea readOnly value={showdown ?? ""}
          style={{
            width: "100%", background: "#0f0f1a", border: "1px solid #2d2d4e",
            borderRadius: 8, color: "#a5f3fc", padding: 12, fontSize: "0.85rem",
            fontFamily: "monospace", resize: "vertical", minHeight: 180,
          }} />
      </div>
    </div>
  );
}
