using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Library;

/// <summary>A library as the rest of the app sees it.</summary>
public sealed record LibraryInfo(int Id, string Name, LibraryKind Kind, string Path, IReadOnlyList<string> Tags, bool Enabled);

/// <summary>
/// The libraries, kept in memory: they're read on every file lookup and scan, and change only when an admin edits
/// them (Admin → Libraries), which calls <see cref="ReloadAsync"/>. The database is the source of truth; the
/// libraries in appsettings.json are only added on the first start, when there are none.
/// </summary>
public sealed class LibraryCatalog(IServiceScopeFactory scopes, IOptions<MediaRiffleOptions> options, ILogger<LibraryCatalog> log)
{
    private volatile IReadOnlyList<LibraryInfo> _all = [];

    public IReadOnlyList<LibraryInfo> All => _all;
    public IEnumerable<LibraryInfo> Enabled => _all.Where(l => l.Enabled);
    public LibraryInfo? Get(int id) => _all.FirstOrDefault(l => l.Id == id);

    /// <summary>Raised after the libraries changed, so the scanner picks up a new or edited one.</summary>
    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Libraries.AnyAsync() && options.Value.Libraries.Count > 0)
            {
                foreach (var l in options.Value.Libraries.Where(l => !string.IsNullOrWhiteSpace(l.Name) && !string.IsNullOrWhiteSpace(l.Path)))
                    db.Libraries.Add(new MediaLibrary { Name = l.Name.Trim(), Kind = l.Kind, Path = l.Path.Trim() });
                await db.SaveChangesAsync();
                log.LogInformation("Added {Count} libraries from the settings file; manage them under Admin → Libraries from now on",
                    options.Value.Libraries.Count);
            }
        }
        await ReloadAsync(raiseChanged: false);
    }

    public async Task ReloadAsync(bool raiseChanged = true)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Libraries.OrderBy(l => l.Name).ToListAsync();
        _all = rows.Select(l => new LibraryInfo(l.Id, l.Name, l.Kind, l.Path, l.TagList, l.Enabled)).ToList();
        if (raiseChanged) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every tag in use by an enabled library, in alphabetical order.</summary>
    public IReadOnlyList<string> Tags => Enabled.SelectMany(l => l.Tags)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The ids of the enabled libraries carrying <paramref name="tag"/>.</summary>
    public HashSet<int> WithTag(string tag) =>
        Enabled.Where(l => l.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Select(l => l.Id).ToHashSet();
}
