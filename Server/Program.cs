using System.Runtime;
using Server;

var logPath = Path.Combine(AppContext.BaseDirectory, "server-crash.log");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var msg = $"[{DateTime.UtcNow:O}] {e.ExceptionObject}\n";
    File.AppendAllText(logPath, msg);
    Console.Error.WriteLine(msg);
};

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

Console.WriteLine($"[db] path={WorldDatabase.DefaultPath}");
using var db = new WorldDatabase();
if (args.Contains("--seed")) { Console.WriteLine("[db] Seeded. Done."); return; }

var server      = new LiteNetServer();
var gameObjects = db.LoadGameObjects();
var gameLoop    = new GameLoop(server, db, gameObjects);
await gameLoop.RunAsync();
