const BASE = "/api";

export interface GameEntry { id: string; name: string }
export interface SpeciesInfo { id: number; name: string; native: boolean }
export interface NatureInfo { id: number; name: string; raisedStat: string; loweredStat: string }
export interface MoveEntry { id: number; name: string; type: string }
export interface AbilityInfo { id: number; name: string; isHidden: boolean }
export interface ItemInfo { id: number; name: string }
export interface EncounterDetail {
  type: string
  levelMin: number
  levelMax: number
  location: string | null
  isEgg: boolean
  shiny: string            // Never | Random | Always | AlwaysStar | AlwaysSquare
  isAlpha: boolean
  fixedGender: number | null
  fixedNature: number | null
  fixedIVs: (number | null)[]
  flawlessIVCount: number
  fixedMoves: number[]
  abilitySlot: string      // Any12 | Any12H | Ability1 | Ability2 | HiddenAbility
  legalBallIds: number[]
  fixedSize: number | null
}
export interface LegalityIssue { field: string; message: string; severity: string }
export interface ValidationResult { isLegal: boolean; report: string; issues: LegalityIssue[] }
export interface GenderOption { value: number; name: string }
export interface PokemonMeta {
  validGenders: GenderOption[]
  statSystem: "EV" | "AV" | "EL"
  statMax: number
  statTotal: number
  hasTeraType: boolean
  canBeShiny: boolean
  hasScale: boolean
  hasAlpha: boolean
  minLevel: number
  maxLevel: number
  alphaMinLevel: number
  alphaMaxLevel: number
}

export interface PokemonConfig {
  game: string
  species: number
  form: number
  level: number
  isShiny: boolean
  gender: number
  nature: number
  ability: number
  heldItem: number
  ball: number
  moves: number[]
  eVs: number[]
  iVs: number[]
  friendship: number
  scale: number
  isAlpha: boolean
  metDate: string
  useCustomOT: boolean
  oT: string
  tID: number
  sID: number
  language: string
  nickname: string
  teraType: number
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`);
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

export const api = {
  getGames: () => get<GameEntry[]>("/gamedata/games"),
  getSpecies: (game: string) => get<SpeciesInfo[]>(`/gamedata/species?game=${game}`),
  getNatures: () => get<NatureInfo[]>("/gamedata/natures"),
  getItems: (game: string) => get<ItemInfo[]>(`/gamedata/items?game=${game}`),
  getBalls: (game: string, species = 0, form = 0) => get<ItemInfo[]>(`/gamedata/balls?game=${game}&species=${species}&form=${form}`),
  getEncounters: (game: string, species: number, form = 0) =>
    get<EncounterDetail[]>(`/pokemon/${species}/encounters?game=${game}&form=${form}`),
  getMoves: (game: string, species: number, form = 0) =>
    get<MoveEntry[]>(`/pokemon/${species}/moves?game=${game}&form=${form}`),
  getAbilities: (game: string, species: number, form = 0) =>
    get<AbilityInfo[]>(`/pokemon/${species}/abilities?game=${game}&form=${form}`),
  getMeta: (game: string, species: number, form = 0) =>
    get<PokemonMeta>(`/pokemon/${species}/meta?game=${game}&form=${form}`),

  validate: async (config: PokemonConfig): Promise<ValidationResult> => {
    const res = await fetch(`${BASE}/pokemon/validate`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(config),
    });
    if (!res.ok) throw new Error(await res.text());
    return res.json();
  },

  generate: async (config: PokemonConfig): Promise<void> => {
    const res = await fetch(`${BASE}/pokemon/generate`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(config),
    });
    if (!res.ok) throw new Error(await res.text());
    const blob = await res.blob();
    const disposition = res.headers.get("content-disposition") ?? "";
    const match = disposition.match(/filename="?([^"]+)"?/);
    const filename = match?.[1] ?? "pokemon.pk9";
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  },
};
