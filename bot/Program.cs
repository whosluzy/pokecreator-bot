using PokecreatorBot;

var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    var file = Path.Combine(AppContext.BaseDirectory, "token.txt");
    if (File.Exists(file)) token = File.ReadAllText(file).Trim();
}
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("No bot token. Set DISCORD_TOKEN env var or put it in token.txt next to the exe.");
    Console.WriteLine("Tip: use the PokeCreator Bot Setup app for a friendly setup window.");
    return;
}

var runner = new BotRunner();
runner.Log += Console.WriteLine;
await runner.StartAsync(token,
    Environment.GetEnvironmentVariable("DISCORD_GUILD"),
    Environment.GetEnvironmentVariable("DISCORD_CHANNEL"));
Console.WriteLine("PokeCreator bot running. Press Ctrl+C to exit.");
await Task.Delay(-1);
