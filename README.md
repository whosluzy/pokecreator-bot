# PokeCreator

Build **legal** Pokémon using the [PKHeX](https://github.com/kwsch/PKHeX) engine,
as a web app **and** a Discord bot with a one-click Windows setup app.

## Projects

| Folder   | What it is |
|----------|------------|
| `api/`   | ASP.NET Core API wrapping `PKHeX.Core` (legality, encounters, Showdown/.pk export). |
| `ui/`    | Next.js web front-end for the creator. |
| `bot/`   | Discord bot (`BotRunner`) — reuses the API's `PkHexService` source directly. |
| `setup/` | WinForms desktop app to configure & run the bot, with a built-in **Update** button. |

The bot and API share the **same** `PkHexService.cs` (linked, not copied), so
legality logic is identical everywhere.

## Run the web app
```
# Terminal 1
cd api && dotnet run --launch-profile http
# Terminal 2
cd ui && npm install && npm run dev   # http://localhost:3000
```

## Run / configure the bot
Build the setup app and run it:
```
cd setup && dotnet run
```
…or grab the prebuilt `pokecreator-setup.exe` from the
[latest release](../../releases/latest). Paste your bot token (and optional
server / channel ID), click **Start Bot**, then use `/create` in Discord.

`config.json` is written next to the exe and stores your settings.

## Updating
The setup app's **Update** button checks the latest GitHub Release and, if a
newer build is published, downloads it and swaps itself automatically.

## Build a release exe
```
cd setup
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ../dist
```
