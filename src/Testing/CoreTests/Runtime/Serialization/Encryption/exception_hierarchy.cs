using Shouldly;
using Wolverine;
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
    public void EncryptionPolicyViolationException_inherits_MessageEncryptionException()
    {
        // Policy violations are not key-related (no key is involved when an
        // envelope is rejected for missing encryption), so KeyId is not on the
        // base class and not on this subclass.
        var ex = new EncryptionPolicyViolationException(new Envelope { MessageType = "X", ContentType = "Y" });
        ex.ShouldBeAssignableTo<MessageEncryptionException>();
    }

    [Fact]
    public void EncryptionPolicyViolationException_message_names_type_and_content_type_only()
    {
        var envelope = new Envelope
        {
            MessageType = "PaymentDetails",
            ContentType = "application/json"
        };

        var ex = new EncryptionPolicyViolationException(envelope);

        ex.Message.ShouldContain("PaymentDetails");
        ex.Message.ShouldContain("application/json");
        ex.Message.ShouldContain("encryption is required");
    }
}
