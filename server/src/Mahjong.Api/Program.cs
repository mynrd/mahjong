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
var extraOrigins = builder.Configuration.GetSection("Mahjong:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddPolicy(LanCors, policy => policy
    // Origins are matched by shape rather than listed one by one. The machine running this gets a
    // different address on every network it joins, and a hardcoded list means the game silently
    // stops working the next time the router hands out a new one. Anything on the loopback or on
    // a private range is allowed; anything routable from the internet is not, so this does not
    // become an open door if the port is ever forwarded.
    .SetIsOriginAllowed(origin => IsLocalOrigin(origin) || extraOrigins.Contains(origin))
    .AllowAnyHeader()
    .AllowAnyMethod()
    // SignalR needs credentials allowed for the websocket handshake to carry the connection id.
    .AllowCredentials()));

static bool IsLocalOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
    if (uri.Scheme is not ("http" or "https")) return false;

    var host = uri.Host;

    if (host is "localhost" or "127.0.0.1" or "[::1]" or "::1") return true;
    if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;

    if (System.Net.IPAddress.IsLoopback(ip)) return true;

    var bytes = ip.GetAddressBytes();

    // The private IPv4 ranges, plus link-local: 10/8, 172.16/12, 192.168/16, 169.254/16.
    if (bytes.Length == 4)
    {
        return bytes[0] switch
        {
            10 => true,
            172 => bytes[1] >= 16 && bytes[1] <= 31,
            192 => bytes[1] == 168,
            169 => bytes[1] == 254,
            _ => false,
        };
    }

    // IPv6 unique-local (fc00::/7) and link-local (fe80::/10).
    return bytes.Length == 16 && ((bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80));
}

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
