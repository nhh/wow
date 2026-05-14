using Client;
using Shared;

var interpolator = new Interpolator();
var netClient    = new LiteNetClient(interpolator);
string host = args.Length > 0 ? args[0] : "127.0.0.1";
netClient.Connect(host, Framing.Port);

var renderer = new Renderer(interpolator);
await renderer.RunAsync(netClient);
