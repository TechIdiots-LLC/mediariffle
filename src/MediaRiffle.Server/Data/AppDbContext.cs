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
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<PlaybackProgress> Progress => Set<PlaybackProgress>();
    public DbSet<UserTag> UserTags => Set<UserTag>();
    public DbSet<HiddenItem> Hidden => Set<HiddenItem>();

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

        // Music: artists and albums go with their library (as its tracks do). No foreign key from a track to its album:
        // adding one to MediaFiles makes SQLite rebuild the whole table on upgrade, and albums are only removed once empty
        b.Entity<Artist>().Property(a => a.Name).UseCollation("NOCASE");
        b.Entity<Artist>().HasIndex(a => new { a.LibraryId, a.Name }).IsUnique();
        b.Entity<Artist>().HasOne<MediaLibrary>().WithMany().HasForeignKey(a => a.LibraryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Album>().HasIndex(a => new { a.LibraryId, a.GroupKey }).IsUnique();
        b.Entity<Album>().HasIndex(a => a.ArtistId);
        b.Entity<Album>().HasOne<MediaLibrary>().WithMany().HasForeignKey(a => a.LibraryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Album>().HasOne<Artist>().WithMany().HasForeignKey(a => a.ArtistId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MediaFile>().HasIndex(f => f.AlbumId);

        b.Entity<PlaybackProgress>().HasKey(p => new { p.UserId, p.MediaFileId });
        b.Entity<PlaybackProgress>().HasIndex(p => new { p.UserId, p.UpdatedUtc });

        // A user's lists go with the user, and an entry with what it tags (a rescan keeps those while their files stay)
        b.Entity<UserTag>().Property(t => t.Tag).HasMaxLength(UserTag.MaxLength).UseCollation("NOCASE");
        b.Entity<UserTag>().HasIndex(t => new { t.UserId, t.Tag });
        b.Entity<UserTag>().HasIndex(t => t.ShowId);
        b.Entity<UserTag>().HasIndex(t => t.MovieId);
        b.Entity<UserTag>().HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserTag>().HasOne<Show>().WithMany().HasForeignKey(t => t.ShowId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserTag>().HasOne<Movie>().WithMany().HasForeignKey(t => t.MovieId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserTag>().HasIndex(t => t.ArtistId);
        b.Entity<UserTag>().HasIndex(t => t.AlbumId);
        b.Entity<UserTag>().HasIndex(t => t.MediaFileId);
        b.Entity<UserTag>().HasOne<Artist>().WithMany().HasForeignKey(t => t.ArtistId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserTag>().HasOne<Album>().WithMany().HasForeignKey(t => t.AlbumId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserTag>().HasOne<MediaFile>().WithMany().HasForeignKey(t => t.MediaFileId).OnDelete(DeleteBehavior.Cascade);

        // Hidden, likewise, goes with the user and with what was hidden
        b.Entity<HiddenItem>().HasIndex(x => new { x.UserId, x.ShowId });
        b.Entity<HiddenItem>().HasIndex(x => new { x.UserId, x.MovieId });
        b.Entity<HiddenItem>().HasIndex(x => new { x.UserId, x.MediaFileId });
        b.Entity<HiddenItem>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HiddenItem>().HasOne<Show>().WithMany().HasForeignKey(x => x.ShowId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HiddenItem>().HasOne<Movie>().WithMany().HasForeignKey(x => x.MovieId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HiddenItem>().HasOne<MediaFile>().WithMany().HasForeignKey(x => x.MediaFileId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HiddenItem>().HasIndex(x => new { x.UserId, x.ArtistId });
        b.Entity<HiddenItem>().HasIndex(x => new { x.UserId, x.AlbumId });
        b.Entity<HiddenItem>().HasOne<Artist>().WithMany().HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HiddenItem>().HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
    }
}
