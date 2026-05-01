using Shouldly;
using Wolverine.Runtime.Serialization.Encryption;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class InMemoryKeyProviderTests
{
    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    [Fact]
    public async Task returns_key_for_known_id()
    {
        var keys = new Dictionary<string, byte[]> { ["k1"] = Key32(0x01) };
        var provider = new InMemoryKeyProvider("k1", keys);

        var result = await provider.GetKeyAsync("k1", CancellationToken.None);

        result.ShouldBe(Key32(0x01));
    }

    [Fact]
    public async Task throws_keynotfound_for_unknown_id()
    {
        var provider = new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x01) });

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await provider.GetKeyAsync("missing", CancellationToken.None));
    }

    [Fact]
    public void default_key_id_is_exposed()
    {
        var provider = new InMemoryKeyProvider("k-default", new Dictionary<string, byte[]> { ["k-default"] = Key32(0x01) });

        provider.DefaultKeyId.ShouldBe("k-default");
    }

    [Fact]
    public void ctor_rejects_keys_not_32_bytes()
    {
        Should.Throw<ArgumentException>(() =>
            new InMemoryKeyProvider("bad", new Dictionary<string, byte[]> { ["bad"] = new byte[16] }));
    }

    [Fact]
    public void ctor_rejects_default_key_not_in_dictionary()
    {
        Should.Throw<ArgumentException>(() =>
            new InMemoryKeyProvider("missing-default", new Dictionary<string, byte[]> { ["other"] = Key32(0x01) }));
    }

    [Fact]
    public async Task constructor_takes_defensive_copy_of_caller_arrays()
    {
        var keyBytes = Key32(0x42);
        var provider = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = keyBytes });

        Array.Clear(keyBytes);

        var stored = await provider.GetKeyAsync("k1", default);
        stored.ShouldAllBe(b => b == 0x42);
    }
}
