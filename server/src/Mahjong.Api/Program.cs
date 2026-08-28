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
builder.Services.AddScoped<UserAuth>();
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

var app = builder.Build();

// This is a LAN game on one machine, so the schema is brought up to date on boot rather than
// through a separate deploy step. It is a no-op once the database matches.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MahjongDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment()) app.MapOpenApi();

// The Angular build writes into wwwroot (see web/angular.json), so one Kestrel serves both the
// page and the API on one port. index.html is told not to cache: every other file is content
// hashed and safe to keep forever, but a stale index.html points at hashed files a new build has
// already deleted, and the game comes up blank until the player clears their cache.
var staticFiles = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name == "index.html")
        {
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
    },
};

app.UseDefaultFiles();
app.UseStaticFiles(staticFiles);

app.MapUserEndpoints();
app.MapRoomEndpoints();
app.MapReplayEndpoints();
app.MapHub<GameHub>("/hubs/game");

app.MapGet("/api/health", async (MahjongDbContext db) => Results.Ok(new
{
    status = "ok",
    database = await db.Database.CanConnectAsync() ? "connected" : "unreachable",
    rooms = await db.Rooms.CountAsync(),
}));

// Client-side routes like /join/ABCD are not files on disk. Anything that reached here without
// matching an endpoint or a file is one of those, so hand back the app and let the router sort it
// out. Registered last, so the real endpoints and the files still win.
//
// The two narrower fallbacks keep the wrong answer out of the API: without them a typo in a path
// or a call that outlived an endpoint gets 200 and a page of HTML, which the client parses as
// JSON and reports as something entirely unrelated to the missing route.
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallback("/hubs/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html", staticFiles);

app.Run();

/// <summary>Exposed so the integration tests can boot the real app with WebApplicationFactory.</summary>
public partial class Program;
