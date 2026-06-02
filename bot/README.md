# PokeCreator Discord Bot

A standalone Discord bot (.exe) that builds legal Pokémon using the **same PKHeX
legality engine** as the PokeCreator web app, and returns the `.trade` Showdown
text. It shares the web app's `PkHexService.cs` directly (no duplicated logic),
but runs completely on its own — the web API does **not** need to be running.

## Setup

1. **Create a bot application**
   - Go to https://discord.com/developers/applications → New Application
   - Bot tab → copy the **Token**
   - Enable nothing special (only the default Guilds intent is used)
   - OAuth2 → URL Generator → scopes: `bot`, `applications.commands` → invite it to your server

2. **Give it the token** (either one):
   - Put the token in a file named `token.txt` next to `pokecreator-bot.exe`, **or**
   - Set the `DISCORD_TOKEN` environment variable

3. *(Optional, for instant command registration)* set `DISCORD_GUILD` to your
   server ID. Without it, the global `/create` command can take up to ~1 hour to
   appear the first time.

## Run

Double-click `bin\Debug\net10.0\pokecreator-bot.exe`, or:

```
cd C:\Users\vitor\Projects\pokecreator\bot
dotnet run
```

Leave it running while you use the bot.

## Use

In Discord, run **`/create`**. You get an interactive panel:

- **Game** dropdown — only Pokémon legal in that game are offered
- **Set Pokémon** button — type the name (and optional level) in a popup
- **Form** dropdown — appears when the Pokémon has regional/alternate forms
- **Nature** dropdown
- **Shiny / Alpha** toggle buttons — auto-disabled when not legal for that Pokémon/game
- **Generate .trade** — replies with the `.trade` Showdown text, ready to copy

Trainer name uses **AutoOT** (the receiving trainer's info is applied on trade).
