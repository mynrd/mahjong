using System.Text.Json.Serialization;
using Mahjong.Api;
using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Mahjong")
    ?? throw new InvalidOperationException("ConnectionStrings:Mahjong is not configured.");

builder.Services.AddDbContext<MahjongDbContext>(options => options.UseSqlServer(connectionString));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums go over the wire as names. Numbers would make the client fragile against any
    // reordering of an enum, and make the payloads unreadable while debugging.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// The house rules a new table starts with come from configuration, so the numbers can be retuned
// without a rebuild. Anything the section leaves out keeps the compiled default in Rules.cs, and
// appsettings.Development.json overrides appsettings.json key by key.
builder.Services.Configure<RuleOptions>(builder.Configuration.GetSection("Mahjong:Rules"));

builder.Services.AddScoped<PlayerAuth>();
builder.Services.AddScoped<ReplayAuth>();
builder.Services.AddScoped<GameService>();

// One registry for the whole process holds the live state of every table, and the ticker closes
// claim windows and moves bots along.
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<GameTicker>();

builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    // The hub serialises with its own options, not the ones set for HTTP. Without this, enums go
    // down the socket as numbers while the REST endpoints send names, and the client ends up
    // needing two different ways to read the same value.
    options.PayloadSerializerOptions = GameJson.Options;
});
builder.Services.AddOpenApi();

const string LanCors = "lan";

builder.Services.AddCors(options => options.AddPolicy(LanCors, policy => policy
    // Every origin is allowed. This is a game run on somebody's laptop for the four people in the
    // room, reached from whatever address the router, the phone hotspot or the tailnet happens to
    // hand out that day, and every attempt to match those by shape ended in a browser refusing to
    // connect for a reason nobody wants to debug mid-game.
    //
    // AllowAnyOrigin cannot be used here: it sends Access-Control-Allow-Origin: *, which the
    // browser rejects together with credentials, and SignalR's handshake carries them. Reflecting
    // whatever Origin arrived is the same permission with a header the browser accepts.
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    // SignalR needs credentials allowed for the websocket handshake to carry the connection id.
    .AllowCredentials()));

var app = builder.Build();

// This is a LAN game on one machine, so the schema is brought up to date on boot rather than
// through a separate deploy step. It is a no-op once the database matches.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MahjongDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseCors(LanCors);

app.MapRoomEndpoints();
app.MapReplayEndpoints();
app.MapHub<GameHub>("/hubs/game");

app.MapGet("/api/health", async (MahjongDbContext db) => Results.Ok(new
{
    status = "ok",
    database = await db.Database.CanConnectAsync() ? "connected" : "unreachable",
    rooms = await db.Rooms.CountAsync(),
}));

app.Run();

/// <summary>Exposed so the integration tests can boot the real app with WebApplicationFactory.</summary>
public partial class Program;
