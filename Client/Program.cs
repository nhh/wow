using Client;
using Shared;

var interpolator = new Interpolator();
var netClient    = new LiteNetClient(interpolator);
netClient.Connect("127.0.0.1", Framing.Port);

var renderer = new Renderer(interpolator);
await renderer.RunAsync(netClient);
