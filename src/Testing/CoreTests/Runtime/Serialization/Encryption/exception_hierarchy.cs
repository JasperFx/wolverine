using Shouldly;
using Wolverine.Runtime.Serialization.Encryption;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class exception_hierarchy
{
    [Fact]
    public void key_not_found_extends_message_encryption_exception()
    {
        var ex = new EncryptionKeyNotFoundException("key-1", new InvalidOperationException("inner"));

        ex.ShouldBeAssignableTo<MessageEncryptionException>();
        ex.KeyId.ShouldBe("key-1");
        ex.InnerException!.Message.ShouldBe("inner");
        ex.Message.ShouldContain("key-1");
    }

    [Fact]
    public void decryption_extends_message_encryption_exception()
    {
        var inner = new System.Security.Cryptography.CryptographicException("tag mismatch");
        var ex = new MessageDecryptionException("key-1", inner);

        ex.ShouldBeAssignableTo<MessageEncryptionException>();
        ex.KeyId.ShouldBe("key-1");
        ex.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void base_class_is_abstract()
    {
        typeof(MessageEncryptionException).IsAbstract.ShouldBeTrue();
    }
}
