using StruxRelay.Data;
using StruxRelay.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Entity Framework logs every statement at Information, which buries anything
// worth reading. The relay's own categories go the other way: a refused device and
// a pipe that was replaced are exactly the lines you need the detail for.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("StruxRelay", LogLevel.Debug);

// The dashboard's only interface. See RelayHub for why the device pipe is not one.
builder.Services.AddSignalR();

// A factory rather than a scoped context: hub calls and the device pipe both want
// one, and a DbContext is not shared across threads.
builder.Services.AddDbContextFactory<RelayDbContext>((services, options) =>
    RelayDatabase.Configure(options, services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<PairingStore>();

var app = builder.Build();

await RelayDatabase.MigrateAsync(app.Services, app.Logger);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<RelayHub>("/hub");

// Liveness only, and the one HTTP endpoint that is not a file: the container
// healthcheck needs something to ask, and a connected device is not a health
// condition.
app.MapGet("/healthz", () => Results.Text("ok\n", "text/plain"));

// Anything else is the relay's own shell. Explicit routes win over the fallback,
// so the device paths added with the pipe are matched before this.
app.MapFallbackToFile("index.html");

app.Run();
