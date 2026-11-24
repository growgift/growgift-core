using FluentAssertions;
using GrowGift.Gifting.Domain;
using Xunit;

namespace GrowGift.Domain.Tests;

public class RedemptionCodeTests
{
    private const string Salt = "test-pepper";

    [Fact]
    public void Generate_produces_expected_format()
    {
        var (_, plaintext) = RedemptionCode.Generate(Salt);
        plaintext.Should().MatchRegex(@"^GG-[2-9A-HJ-NP-TV-Z]{4}-[2-9A-HJ-NP-TV-Z]{4}-[2-9A-HJ-NP-TV-Z]{4}$");
    }

    [Fact]
    public void Generate_never_repeats_in_small_sample()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 10_000; i++)
        {
            var (_, plaintext) = RedemptionCode.Generate(Salt);
            seen.Add(plaintext).Should().BeTrue("codes must not collide");
        }
    }

    [Fact]
    public void Lookalike_characters_normalize_to_same_hash()
    {
        var canonical = RedemptionCode.FromPlaintext("GG-84KQ-29MN-XP8D", Salt);
        var sloppy = RedemptionCode.FromPlaintext("gg 84kq 29mn xp8d", Salt);
        sloppy.HashHex.Should().Be(canonical.HashHex);
    }

    [Fact]
    public void Different_salts_produce_different_hashes()
    {
        var a = RedemptionCode.FromPlaintext("GG-84KQ-29MN-XP8D", "salt-a");
        var b = RedemptionCode.FromPlaintext("GG-84KQ-29MN-XP8D", "salt-b");
        a.HashHex.Should().NotBe(b.HashHex);
    }
}
