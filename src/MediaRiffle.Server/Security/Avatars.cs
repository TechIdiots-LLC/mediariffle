using MediaRiffle.Server.Data;

namespace MediaRiffle.Server.Security;

/// <summary>
/// Profile pictures: one of <see cref="BuiltIn"/> built-in ones (a cartoon animal or food on a colour, drawn by the page,
/// avatar.js), or the user's own photo, which the page crops and shrinks to a small JPEG before sending it.
/// </summary>
public static class Avatars
{
    /// <summary>How many built-in pictures there are (avatar.js has the same list).</summary>
    public const int BuiltIn = 24;
    /// <summary>The largest photo taken: the page sends 256×256 JPEGs of a few tens of kB.</summary>
    public const int MaxPhotoBytes = 512 * 1024;

    /// <summary>What the page needs to draw a user's picture: a built-in one's number, or their photo's address.</summary>
    public static object Of(AppUser user) => user.Avatar switch
    {
        { } a when a.StartsWith("photo:", StringComparison.Ordinal) => new { photo = $"/api/avatars/{user.Id}?v={a[6..]}" },
        { } a when a.StartsWith("emoji:", StringComparison.Ordinal) && int.TryParse(a[6..], out var n) && n is >= 0 and < BuiltIn
            => new { emoji = n },
        _ => new { emoji = DefaultFor(user.Id) },
    };

    /// <summary>The built-in picture someone has until they choose: random-looking, the same every time for them.</summary>
    public static int DefaultFor(string userId)
    {
        // Not string.GetHashCode, which changes from run to run
        uint h = 2166136261;
        foreach (var c in userId) h = (h ^ c) * 16777619;
        return (int)(h % BuiltIn);
    }

    /// <summary>Whether these bytes are a JPEG (what the page sends), by its first bytes.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
}
