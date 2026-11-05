using System.Security.Cryptography;
using MediaRiffle.Server.Data;

namespace MediaRiffle.Server.Security;

public enum PairingState { Waiting, Linked, Gone }

/// <summary>
/// Linking a device that has no browser (a TV, a console) to an account, as TV apps do (OAuth's device authorization
/// grant, RFC 8628): the device asks for a code and shows it, the user enters it on MediaRiffle in a browser where
/// they're signed in (/link), and the device, asking every few seconds, then gets its own token
/// (<see cref="DeviceTokens"/>). Codes wait here, in memory: they last minutes, and a restart only means starting again
/// on the device.
/// </summary>
public sealed class DevicePairings(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    /// <summary>How often a device should ask whether its code was entered.</summary>
    public const int PollSeconds = 5;
    /// <summary>Codes waiting at once, so a flood of them can't fill the memory (asking is rate limited too).</summary>
    public const int MaxWaiting = 200;
    public const int CodeLength = 8;
    // Consonants and digits, as RFC 8628 suggests: no vowels (so no words), and no 0, 1, O or I to mix up. 28 to the
    // 8th is about 38 bits, against a guess rate limited per address and codes that last 10 minutes
    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ23456789";

    public sealed class Pairing
    {
        /// <summary>The device's secret, which it asks with; never shown.</summary>
        public required string DeviceCode { get; init; }
        /// <summary>What the user types in (without the dash).</summary>
        public required string UserCode { get; init; }
        public required string Name { get; init; }
        public string? Address { get; init; }
        public DateTimeOffset Expires { get; init; }
        /// <summary>Who entered the code, once someone has.</summary>
        public string? UserId { get; set; }
    }

    private readonly Lock sync = new();
    private readonly Dictionary<string, Pairing> byUserCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pairing> byDeviceCode = new(StringComparer.Ordinal);

    /// <summary>A new code for a device, or null when too many are waiting.</summary>
    public Pairing? Start(string? name, string? address)
    {
        lock (sync)
        {
            Purge();
            if (byDeviceCode.Count >= MaxWaiting) return null;
            string userCode;
            do userCode = RandomNumberGenerator.GetString(Alphabet, CodeLength);
            while (byUserCode.ContainsKey(userCode));
            var p = new Pairing
            {
                DeviceCode = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
                UserCode = userCode, Name = CleanName(name), Address = address,
                Expires = clock.GetUtcNow() + Lifetime,
            };
            byUserCode[p.UserCode] = p;
            byDeviceCode[p.DeviceCode] = p;
            return p;
        }
    }

    /// <summary>The device waiting with this code (as typed: any case, with or without the dash), or null.</summary>
    public Pairing? Find(string? userCode)
    {
        lock (sync)
        {
            Purge();
            return byUserCode.TryGetValue(Normalize(userCode), out var p) && p.UserId is null ? p : null;
        }
    }

    /// <summary>Links the device waiting with this code to the user; false if there's none (wrong, used, run out).</summary>
    public bool Approve(string? userCode, string userId)
    {
        lock (sync)
        {
            Purge();
            if (!byUserCode.TryGetValue(Normalize(userCode), out var p) || p.UserId is not null) return false;
            p.UserId = userId;
            return true;
        }
    }

    /// <summary>
    /// The device asking: still waiting; linked (once: the code is used up, and the user and name come back); or gone
    /// (unknown, or run out).
    /// </summary>
    public (PairingState State, string? UserId, string? Name) Poll(string? deviceCode)
    {
        lock (sync)
        {
            Purge();
            if (deviceCode is null || !byDeviceCode.TryGetValue(deviceCode, out var p)) return (PairingState.Gone, null, null);
            if (p.UserId is null) return (PairingState.Waiting, null, null);
            Remove(p);
            return (PairingState.Linked, p.UserId, p.Name);
        }
    }

    /// <summary>A code as shown: BCDF-2345.</summary>
    public static string Display(string userCode) => $"{userCode[..4]}-{userCode[4..]}";

    /// <summary>A code as typed, as stored: upper case, letters and digits only.</summary>
    public static string Normalize(string? userCode) =>
        new((userCode ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>The name a device gave itself, fit to show: no control characters, not too long, never empty.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > LinkedDevice.MaxName) clean = clean[..LinkedDevice.MaxName].TrimEnd();
        return clean.Length > 0 ? clean : "Device";
    }

    private void Purge()
    {
        var now = clock.GetUtcNow();
        foreach (var p in byDeviceCode.Values.Where(p => p.Expires <= now).ToList()) Remove(p);
    }

    private void Remove(Pairing p)
    {
        byUserCode.Remove(p.UserCode);
        byDeviceCode.Remove(p.DeviceCode);
    }
}
