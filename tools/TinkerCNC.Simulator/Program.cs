// CNC machine simulator, for developing TinkerCNC without hardware.
//
//   dotnet run --project tools/TinkerCNC.Simulator -- [--name Simulator] [--ip 127.0.0.1] [--speed 10] [--job] [--query-path-only] [--legacy-line-counter]
//
//   UDP 3333  announcements "name,ip,port,busy" once per second (broadcast + loopback)
//   TCP 2222  framed packets: status '?', realtime chars, text commands ($H, $X, jogs, diagnose,
//             "play <path>", "abort", and "alarm" to simulate an alarm); 0xB7 running-file reply,
//             XMODEM-like downloads; --job starts the demo job (/sd/gcodes/demo_part.nc) at startup
//   WS  82    /ws_video   "start_stream" -> JPEG frames of a top view of the bed
//             /ws_preview vpreview protocol (open / meta / play / pause / resume / stop)

using TinkerCNC.Simulator;

var options = SimulatorOptions.Parse(args);
var machine = new SimMachine { FeedOverridePercent = options.Speed * 100 };
var files = new SimFileSystem();
var context = new SimContext(machine, new JobPlayer(machine, files, options.Speed, options.LegacyLineCounter, options.ManualToolChange), files, options);
var renderer = new FrameRenderer();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{options.VideoPort}");
var app = builder.Build();
app.UseWebSockets();

app.Map("/ws_video", ctx => WebSocketEndpoint.RunAsync(ctx, ws => new LiveVideoSession(ws, machine, renderer).RunAsync(ctx.RequestAborted)));
app.Map("/ws_preview", ctx => WebSocketEndpoint.RunAsync(ctx, ws => new PreviewSession(ws, renderer).RunAsync(ctx.RequestAborted)));

_ = Task.Run(() => new Announcer(options, machine).RunAsync(app.Lifetime.ApplicationStopping));
_ = Task.Run(() => new ControlServer(context).RunAsync(app.Lifetime.ApplicationStopping));
if (options.StartJob) context.Job.Start(SampleJob.Path);

Console.WriteLine($"Simulator '{options.Name}' on {options.Ip}: control tcp/2222, discovery udp/3333, video ws/{options.VideoPort}");
app.Run();
