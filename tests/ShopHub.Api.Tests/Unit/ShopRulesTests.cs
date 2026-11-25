using System.Security.Cryptography;
using System.Text;
using ShopHub.Api.Shops;

namespace ShopHub.Api.Tests.Unit;

/// <summary>data-model.md <c>shops</c> rules (FR-006): name, enums, wallet address, generated ids.</summary>
public sealed class ShopRulesTests
{
    private const string Valid = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";

    // EIP-55's own examples, also used by shop-operator's wallet tests.
    [Theory]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    [InlineData("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB")]
    [InlineData("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb")]
    public void ChecksummedAddressesPassAndStayAsTheyAre(string address)
    {
        Assert.Equal(address, Eip55.Normalize(address, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("0xfb6916095ca1df60bb79ce92ce3ea74c37c5d359", "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("0xFB6916095CA1DF60BB79CE92CE3EA74C37C5D359", "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("  0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed ", Valid)]
    public void UniformCaseHasNoChecksumAndIsStoredChecksummed(string input, string stored)
    {
        Assert.Equal(stored, Eip55.Normalize(input, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void MixedCaseWithAWrongLetterFailsTheChecksum()
    {
        Assert.Null(Eip55.Normalize("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAEd", out var problem));
        Assert.Equal(Eip55.ChecksumProblem, problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeZ")]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed00")]
    [InlineData("0X5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    public void AnythingElseIsAFormatProblem(string? input)
    {
        Assert.Null(Eip55.Normalize(input, out var problem));
        Assert.Equal(Eip55.FormatProblem, problem);
    }

    [Fact]
    public void KeccakMatchesKnownDigests()
    {
        Assert.Equal("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470",
            Convert.ToHexStringLower(Keccak.Hash256([])));
        Assert.Equal("4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45",
            Convert.ToHexStringLower(Keccak.Hash256("abc"u8)));
    }

    /// <summary>The same sponge with SHA3's domain byte must equal the platform's SHA3-256, across block boundaries.</summary>
    [Fact]
    public void SpongeMatchesPlatformSha3AcrossBlockSizes()
    {
        Assert.SkipUnless(SHA3_256.IsSupported, "SHA3-256 not available on this platform");
        foreach (var length in new[] { 0, 1, 135, 136, 137, 271, 272, 500 })
        {
            var data = Encoding.ASCII.GetBytes(new string('a', length));
            Assert.Equal(SHA3_256.HashData(data), Keccak.Sponge(data, 0x06));
        }
    }

    [Theory]
    [InlineData("Acme", "Acme")]
    [InlineData("  Acme Clothing  ", "Acme Clothing")]
    [InlineData("Ćevapi & Co. 🍔", "Ćevapi & Co. 🍔")]
    public void NamesAreTrimmed(string input, string expected)
    {
        Assert.Equal(expected, ShopRules.Name(input, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void NameLengthCountsCodePoints()
    {
        Assert.NotNull(ShopRules.Name(new string('a', 100), out _));
        Assert.Null(ShopRules.Name(new string('a', 101), out _));
        // 100 emoji are 200 UTF-16 units but 100 characters for PostgreSQL and the Shop CRD.
        Assert.NotNull(ShopRules.Name(string.Concat(Enumerable.Repeat("🍔", 100)), out _));
        Assert.Null(ShopRules.Name(string.Concat(Enumerable.Repeat("🍔", 101)), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\nbreak")]
    [InlineData("Tab\there")]
    [InlineData("Nul\0")]
    public void EmptyOrControlCharacterNamesAreRefused(string? input)
    {
        Assert.Null(ShopRules.Name(input, out var problem));
        Assert.Equal(ShopRules.NameProblem, problem);
    }

    [Fact]
    public void NameKeyIgnoresCaseAndSurroundingSpaces() =>
        Assert.Equal(ShopRules.NameKey("acme clothing"), ShopRules.NameKey("  ACME Clothing "));

    [Fact]
    public void ValidateReturnsTheNormalisedShop()
    {
        var shop = ShopRules.Validate(" Acme ", "high", "light", "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", out var problems);

        Assert.Empty(problems);
        Assert.Equal(new NewShop("Acme", Availability.High, DatabaseKind.Light, Valid), shop);
    }

    [Fact]
    public void ValidateReportsEveryFieldByItsRequestName()
    {
        var shop = ShopRules.Validate("", "medium", "mysql", "0x12", out var problems);

        Assert.Null(shop);
        Assert.Equal(["availability", "database", "name", "walletAddress"], problems.Keys.Order());
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("HIGH")]
    [InlineData(" high")]
    [InlineData("medium")]
    [InlineData(null)]
    public void AvailabilityIsExactLowercase(string? value) => Assert.False(Availability.IsValid(value));

    [Theory]
    [InlineData("Standard")]
    [InlineData("LIGHT")]
    [InlineData("light ")]
    [InlineData("redis")]
    [InlineData(null)]
    public void DatabaseIsExactLowercase(string? value) => Assert.False(DatabaseKind.IsValid(value));

    [Fact]
    public void GeneratedIdsHaveTheShopIdFormatAndDiffer()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => ShopIds.New()).ToList();

        Assert.All(ids, id => Assert.True(ShopNames.IsValidId(id), id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // Every character of the alphabet shows up, so none is accidentally excluded.
        Assert.Equal(32, ids.SelectMany(id => id[1..]).Distinct().Count());
    }
}
