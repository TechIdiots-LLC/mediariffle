using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<PlaybackProgress> Progress => Set<PlaybackProgress>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Invite>().HasIndex(i => i.TokenHash).IsUnique();

        b.Entity<Show>().HasIndex(s => new { s.Library, s.Folder }).IsUnique();
        b.Entity<Season>().HasIndex(s => new { s.ShowId, s.Number }).IsUnique();
        b.Entity<Movie>().HasIndex(m => new { m.Library, m.GroupKey }).IsUnique();

        b.Entity<MediaFile>().HasIndex(f => new { f.Library, f.RelativePath }).IsUnique();
        b.Entity<MediaFile>().HasIndex(f => f.ShowId);
        b.Entity<MediaFile>().HasIndex(f => f.SeasonId);
        b.Entity<MediaFile>().HasIndex(f => f.MovieId);
        b.Entity<MediaFile>().HasOne<Movie>().WithMany(m => m.Parts).HasForeignKey(f => f.MovieId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MediaFile>().HasOne<Show>().WithMany().HasForeignKey(f => f.ShowId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MediaFile>().HasOne<Season>().WithMany().HasForeignKey(f => f.SeasonId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<PlaybackProgress>().HasKey(p => new { p.UserId, p.MediaFileId });
        b.Entity<PlaybackProgress>().HasIndex(p => new { p.UserId, p.UpdatedUtc });
    }
}
