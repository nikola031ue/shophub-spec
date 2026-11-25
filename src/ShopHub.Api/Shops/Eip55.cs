using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopHub.Api.Shops;

/// <summary>
/// Wallet address check (FR-006, data-model.md <c>shops.wallet_address</c>), the same rule as
/// shop-operator's <c>wallet.NormalizeAddress</c>: <c>0x</c> + 40 hex digits; all-lowercase and
/// all-uppercase carry no checksum and are accepted; mixed case must match EIP-55 exactly. The
/// result is the EIP-55 form, which is what ShopHub stores and sends to the Wallet.
/// </summary>
public static partial class Eip55
{
    public const string FormatProblem = "Enter a wallet address: 0x followed by 40 hexadecimal digits.";
    public const string ChecksumProblem = "This wallet address has a typo: its letter case doesn't match its checksum.";

    /// <summary>The checksummed address, or null with the problem text.</summary>
    public static string? Normalize(string? address, out string? problem)
    {
        var a = address?.Trim() ?? "";
        if (!Format().IsMatch(a))
        {
            problem = FormatProblem;
            return null;
        }

        var digits = a[2..];
        var checksummed = Checksum(digits);
        var uniformCase = !digits.Any(char.IsAsciiLetterUpper) || !digits.Any(char.IsAsciiLetterLower);
        if (!uniformCase && a != checksummed)
        {
            problem = ChecksumProblem;
            return null;
        }

        problem = null;
        return checksummed;
    }

    /// <summary>EIP-55: hex letter i is uppercase when nibble i of keccak256(lowercase hex) is 8 or more.</summary>
    private static string Checksum(string digits)
    {
        var lower = digits.ToLowerInvariant();
        var hash = Keccak.Hash256(Encoding.ASCII.GetBytes(lower));
        var sb = new StringBuilder("0x", 42);
        for (var i = 0; i < lower.Length; i++)
        {
            var nibble = (hash[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF;
            sb.Append(nibble >= 8 ? char.ToUpper(lower[i], CultureInfo.InvariantCulture) : lower[i]);
        }

        return sb.ToString();
    }

    [GeneratedRegex("^0x[0-9a-fA-F]{40}$")]
    private static partial Regex Format();
}

/// <summary>
/// Keccak-256 as Ethereum uses it (original Keccak padding 0x01, not SHA3's 0x06; .NET's SHA3_256
/// is therefore not usable for EIP-55). Small and allocation-light; only short inputs are hashed.
/// </summary>
public static class Keccak
{
    private const int Rate = 136; // 1088-bit rate for 256-bit output

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000,
        0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
        0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
        0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
        0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    ];

    // Rotation offsets indexed by x + 5y.
    private static readonly int[] Rotations =
    [
        0, 1, 62, 28, 27,
        36, 44, 6, 55, 20,
        3, 10, 43, 25, 39,
        41, 45, 15, 21, 8,
        18, 2, 61, 56, 14,
    ];

    public static byte[] Hash256(ReadOnlySpan<byte> data) => Sponge(data, 0x01);

    /// <summary>The sponge with a chosen domain byte; 0x06 gives SHA3-256 (used by the tests as a cross-check).</summary>
    public static byte[] Sponge(ReadOnlySpan<byte> data, byte domain)
    {
        Span<ulong> state = stackalloc ulong[25];
        while (data.Length >= Rate)
        {
            Absorb(state, data[..Rate]);
            data = data[Rate..];
        }

        Span<byte> last = stackalloc byte[Rate];
        last.Clear();
        data.CopyTo(last);
        last[data.Length] ^= domain;
        last[Rate - 1] ^= 0x80;
        Absorb(state, last);

        var output = new byte[32];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(i * 8), state[i]);
        }

        return output;
    }

    private static void Absorb(Span<ulong> state, ReadOnlySpan<byte> block)
    {
        for (var i = 0; i < Rate / 8; i++)
        {
            state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        }

        Permute(state);
    }

    private static void Permute(Span<ulong> s)
    {
        Span<ulong> c = stackalloc ulong[5];
        Span<ulong> b = stackalloc ulong[25];
        foreach (var rc in RoundConstants)
        {
            // θ
            for (var x = 0; x < 5; x++)
            {
                c[x] = s[x] ^ s[x + 5] ^ s[x + 10] ^ s[x + 15] ^ s[x + 20];
            }

            for (var x = 0; x < 5; x++)
            {
                var d = c[(x + 4) % 5] ^ ulong.RotateLeft(c[(x + 1) % 5], 1);
                for (var y = 0; y < 25; y += 5)
                {
                    s[y + x] ^= d;
                }
            }

            // ρ and π
            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    b[y + (5 * (((2 * x) + (3 * y)) % 5))] = ulong.RotateLeft(s[x + (5 * y)], Rotations[x + (5 * y)]);
                }
            }

            // χ
            for (var y = 0; y < 25; y += 5)
            {
                for (var x = 0; x < 5; x++)
                {
                    s[y + x] = b[y + x] ^ (~b[y + ((x + 1) % 5)] & b[y + ((x + 2) % 5)]);
                }
            }

            // ι
            s[0] ^= rc;
        }
    }
}
