using Microsoft.EntityFrameworkCore;

namespace Mahjong.Infrastructure;

public class MahjongDbContext(DbContextOptions<MahjongDbContext> options) : DbContext(options)
{
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameAction> GameActions => Set<GameAction>();
    public DbSet<GameFrame> GameFrames => Set<GameFrame>();
    public DbSet<HandResult> HandResults => Set<HandResult>();
    public DbSet<SettlementRow> Settlements => Set<SettlementRow>();
    public DbSet<ReplayToken> ReplayTokens => Set<ReplayToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Room>(room =>
        {
            room.HasIndex(r => r.Code).IsUnique();
            room.Property(r => r.Code).IsFixedLength();
            room.Property(r => r.RulesJson).HasColumnType("nvarchar(max)");

            room.HasMany(r => r.Players)
                .WithOne(p => p.Room!)
                .HasForeignKey(p => p.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            room.HasMany(r => r.Games)
                .WithOne(g => g.Room!)
                .HasForeignKey(g => g.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            room.HasMany(r => r.ReplayTokens)
                .WithOne(t => t.Room!)
                .HasForeignKey(t => t.RoomId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Player>(player =>
        {
            // Two players can never end up on the same seat, however concurrent the joins are.
            // This is the constraint that makes the join endpoint safe under a race rather than
            // the check-then-insert in application code, which has a window.
            player.HasIndex(p => new { p.RoomId, p.Seat }).IsUnique();
            player.HasIndex(p => p.TokenHash);
        });

        model.Entity<Game>(game =>
        {
            game.HasIndex(g => new { g.RoomId, g.HandNumber }).IsUnique();
            game.Property(g => g.StateJson).HasColumnType("nvarchar(max)");

            game.HasMany(g => g.Actions)
                .WithOne(a => a.Game!)
                .HasForeignKey(a => a.GameId)
                .OnDelete(DeleteBehavior.Cascade);

            game.HasMany(g => g.Frames)
                .WithOne(f => f.Game!)
                .HasForeignKey(f => f.GameId)
                .OnDelete(DeleteBehavior.Cascade);

            game.HasOne(g => g.Result)
                .WithOne(r => r.Game!)
                .HasForeignKey<HandResult>(r => r.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GameAction>(action =>
        {
            action.HasIndex(a => new { a.GameId, a.Seq }).IsUnique();
            action.Property(a => a.PayloadJson).HasColumnType("nvarchar(max)");
        });

        model.Entity<GameFrame>(frame =>
        {
            frame.HasIndex(f => new { f.GameId, f.AfterSeq }).IsUnique();
            frame.Property(f => f.StateJson).HasColumnType("nvarchar(max)");
        });

        model.Entity<ReplayToken>(token =>
        {
            token.HasIndex(t => t.TokenHash);
        });

        model.Entity<HandResult>(result =>
        {
            result.Property(r => r.BreakdownJson).HasColumnType("nvarchar(max)");

            result.HasMany(r => r.Settlements)
                .WithOne(s => s.HandResult!)
                .HasForeignKey(s => s.HandResultId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SettlementRow>(settlement =>
        {
            settlement.HasIndex(s => s.GameId);
            settlement.HasIndex(s => s.PlayerId);
        });
    }
}
