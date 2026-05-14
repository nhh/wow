using System.Runtime;
using Server;

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

Console.WriteLine($"[db] path={WorldDatabase.DefaultPath}");
using var db = new WorldDatabase();
if (args.Length > 0 && int.TryParse(args[0], out int overrideCount))
    db.ParticleCount = overrideCount;

var server   = new LiteNetServer();
var gameLoop = new GameLoop(server, db);
await gameLoop.RunAsync();
