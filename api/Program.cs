using System.Text.Json;
using System.Text.Json.Serialization;
using StruxRelay.Data;
using StruxRelay.Devices;
using StruxRelay.Hubs;
using StruxRelay.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Entity Framework logs every statement at Information, which buries anything
// worth reading. The relay's own categories go the other way: a refused device and
// a pipe that was replaced are exactly the lines you need the detail for.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("StruxRelay", LogLevel.Debug);

// The dashboard's only interface. See RelayHub for why the device pipe is not one.
builder.Services.AddSignalR()
    // Enums as names, not numbers. The default would put connection: 1 on the
    // wire, which forces the browser to keep a copy of the enum's ORDER — the one
    // thing about a C# enum that can change without anybody noticing.
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// A factory rather than a scoped context: hub calls and the device pipe both want
// one, and a DbContext is not shared across threads.
builder.Services.AddDbContextFactory<RelayDbContext>((services, options) =>
    RelayDatabase.Configure(options, services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<PairingStore>();

// Singletons because a device's pipe outlives any request but the one holding it.
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<DeviceDirectory>();

// Telemetry. Bound the ordinary way, so appsettings.json and
// Relay__Telemetry__Influx__Token both work with nothing custom.
builder.Services.Configure<TelemetryOptions>(
    builder.Configuration.GetSection(TelemetryOptions.Section));
builder.Services.AddHttpClient(nameof(InfluxTelemetrySink));

// The sink is registered behind its interface, which is the whole point: adding a
// second destination is a registration here and a class beside the Influx one,
// with nothing in the router or the hub to change.
builder.Services.AddSingleton<ITelemetrySink, InfluxTelemetrySink>();

// One instance, two roles: the pipe ingests into it and the host runs its flush
// loop, so it is registered as itself and then handed to AddHostedService rather
// than constructed twice.
builder.Services.AddSingleton<TelemetryRouter>();
builder.Services.AddHostedService(services => services.GetRequiredService<TelemetryRouter>());

var app = builder.Build();

await RelayDatabase.MigrateAsync(app.Services, app.Logger);

// The device pipe is a raw socket, not a hub, so the upgrade is handled here.
app.UseWebSockets(new WebSocketOptions
{
    // Ping, and require an answer. The device does reply: ESP-IDF's WebSocket
    // transport handles PING internally and sends a PONG (transport_ws.c),
    // BEFORE the firmware's own read loop sees the frame — so the fact that
    // RelaySocket::ReadFrame discards non-binary frames does not mean pings go
    // unanswered. Worth stating because assuming otherwise is what left this
    // without a timeout at first.
    //
    // With both set, a device that vanishes without closing — power cut, WiFi
    // gone — is noticed within roughly one interval plus one timeout rather
    // than whenever TCP retransmission happens to give up, which is minutes and
    // not ours to control.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    KeepAliveTimeout = TimeSpan.FromSeconds(20),
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<RelayHub>("/hub");

// The device's outbound pipe. Mapped BEFORE the SPA fallback, and the reason is
// not style: without a route here the fallback answered a device's upgrade request
// with index.html, and the device reported "relay refused the upgrade with HTTP
// 200" — a refusal that names the wrong cause.
app.MapGet("/device", DevicePipe.HandleAsync);

// Liveness only, and the one HTTP endpoint that is not a file: the container
// healthcheck needs something to ask, and a connected device is not a health
// condition.
app.MapGet("/healthz", () => Results.Text("ok\n", "text/plain"));

// Anything else is the relay's own shell.
app.MapFallbackToFile("index.html");

app.Run();
