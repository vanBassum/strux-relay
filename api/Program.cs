using System.Text.Json;
using System.Text.Json.Serialization;
using StruxRelay.Data;
using StruxRelay.Devices;
using StruxRelay.Hubs;

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

var app = builder.Build();

await RelayDatabase.MigrateAsync(app.Services, app.Logger);

// The device pipe is a raw socket, not a hub, so the upgrade is handled here.
app.UseWebSockets(new WebSocketOptions
{
    // Note what this does NOT buy: the firmware discards every non-binary frame
    // (RelaySocket::ReadFrame), so it never pongs a server ping and a missing
    // pong says nothing — which is why no KeepAliveTimeout is set here, as one
    // would tear down healthy pipes. What it does buy is the send itself
    // failing, which surfaces a dead TCP connection on this side.
    //
    // Liveness in the other direction is the device's job and it already does
    // it: RelayManager pings every 30 s and drops the pipe when one will not go
    // out. So an open pipe here means the device was alive within 30 s.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
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
