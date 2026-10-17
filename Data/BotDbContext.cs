using Microsoft.EntityFrameworkCore;
using MintVkBot.Models;

namespace MintVkBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<ChatSettings> ChatSettings => Set<ChatSettings>();

    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ChatMember>()
            .HasIndex(m => new { m.PeerId, m.UserId })
            .IsUnique();
    }
}
