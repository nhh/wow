using Server;
using Shared;

int particleCount = args.Length > 0 ? int.Parse(args[0]) : Framing.ParticleCount;

var server   = new LiteNetServer();
var gameLoop = new GameLoop(server, particleCount);
await gameLoop.RunAsync();
