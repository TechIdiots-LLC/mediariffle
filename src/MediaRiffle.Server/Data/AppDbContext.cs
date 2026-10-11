using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<MediaLibrary> Libraries => Set<MediaLibrary>();
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<PlaybackProgress> Progress => Set<PlaybackProgress>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Invite>().HasIndex(i => i.TokenHash).IsUnique();

        b.Entity<MediaLibrary>().HasIndex(l => l.Name).IsUnique();
        // Removing a library removes everything scanned from it
        b.Entity<Show>().HasOne<MediaLibrary>().WithMany().HasForeignKey(s => s.LibraryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Movie>().HasOne<MediaLibrary>().WithMany().HasForeignKey(m => m.LibraryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MediaFile>().HasOne<MediaLibrary>().WithMany().HasForeignKey(f => f.LibraryId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Show>().HasIndex(s => new { s.LibraryId, s.Folder }).IsUnique();
        b.Entity<Season>().HasIndex(s => new { s.ShowId, s.Number }).IsUnique();
        b.Entity<Movie>().HasIndex(m => new { m.LibraryId, m.GroupKey }).IsUnique();

        b.Entity<MediaFile>().HasIndex(f => new { f.LibraryId, f.RelativePath }).IsUnique();
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
