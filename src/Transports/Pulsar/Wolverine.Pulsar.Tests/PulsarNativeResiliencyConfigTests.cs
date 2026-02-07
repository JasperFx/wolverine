using DotPulsar;
using Shouldly;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// Unit tests for Pulsar Native Resiliency configuration classes.
/// These tests verify the configuration logic without requiring a live Pulsar broker.
/// </summary>
public class PulsarNativeResiliencyConfigTests
{
    #region DeadLetterTopic Tests

    [Fact]
    public void DeadLetterTopic_DefaultNative_Should_Have_NativeMode()
    {
        var dlq = DeadLetterTopic.DefaultNative;
        
        dlq.Mode.ShouldBe(DeadLetterTopicMode.Native);
        dlq.TopicName.ShouldBeNull();
    }

    [Fact]
    public void DeadLetterTopic_With_Custom_TopicName_Should_Preserve_Name()
    {
        var dlq = new DeadLetterTopic("custom-dlq", DeadLetterTopicMode.Native);
        
        dlq.TopicName.ShouldBe("custom-dlq");
        dlq.Mode.ShouldBe(DeadLetterTopicMode.Native);
    }

    [Fact]
    public void DeadLetterTopic_TopicName_Setter_Should_Throw_On_Null()
    {
        var dlq = new DeadLetterTopic(DeadLetterTopicMode.Native);
        
        Should.Throw<ArgumentNullException>(() => dlq.TopicName = null!);
    }

    [Fact]
    public void DeadLetterTopic_GetHashCode_Should_Handle_Null_TopicName()
    {
        var dlq = new DeadLetterTopic(DeadLetterTopicMode.Native);
        
        // Should not throw and return 0 for null topic name
        var hash = dlq.GetHashCode();
        hash.ShouldBe(0);
    }

    [Fact]
    public void DeadLetterTopic_GetHashCode_Should_Return_Consistent_Value()
    {
        var dlq = new DeadLetterTopic("test-dlq", DeadLetterTopicMode.Native);
        
        var hash1 = dlq.GetHashCode();
        var hash2 = dlq.GetHashCode();
        
        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void DeadLetterTopic_Equals_Should_Compare_TopicNames()
    {
        var dlq1 = new DeadLetterTopic("test-dlq", DeadLetterTopicMode.Native);
        var dlq2 = new DeadLetterTopic("test-dlq", DeadLetterTopicMode.WolverineStorage);
        var dlq3 = new DeadLetterTopic("other-dlq", DeadLetterTopicMode.Native);
        
        dlq1.Equals(dlq2).ShouldBeTrue(); // Same topic name
        dlq1.Equals(dlq3).ShouldBeFalse(); // Different topic name
    }

    #endregion

    #region RetryLetterTopic Tests

    [Fact]
    public void RetryLetterTopic_DefaultNative_Should_Have_Default_Retries()
    {
        var retry = RetryLetterTopic.DefaultNative;
        
        retry.Retry.Count.ShouldBe(3);
        retry.Retry[0].ShouldBe(TimeSpan.FromSeconds(2));
        retry.Retry[1].ShouldBe(TimeSpan.FromSeconds(5));
        retry.Retry[2].ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void RetryLetterTopic_Should_Preserve_Custom_Retries()
    {
        var retries = new List<TimeSpan>
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10)
        };
        
        var retry = new RetryLetterTopic(retries);
        
        retry.Retry.Count.ShouldBe(3);
        retry.Retry[0].ShouldBe(TimeSpan.FromSeconds(1));
        retry.Retry[1].ShouldBe(TimeSpan.FromSeconds(5));
        retry.Retry[2].ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void RetryLetterTopic_Retry_Should_Return_Copy_Of_List()
    {
        var retries = new List<TimeSpan> { TimeSpan.FromSeconds(1) };
        var retry = new RetryLetterTopic(retries);
        
        var retrievedRetries = retry.Retry;
        retrievedRetries.Add(TimeSpan.FromSeconds(999));
        
        // Original should not be modified
        retry.Retry.Count.ShouldBe(1);
    }

    [Fact]
    public void RetryLetterTopic_GetHashCode_Should_Handle_Null_TopicName()
    {
        var retry = new RetryLetterTopic(new List<TimeSpan> { TimeSpan.FromSeconds(1) });
        
        // Should not throw and return 0 for null topic name
        var hash = retry.GetHashCode();
        hash.ShouldBe(0);
    }

    [Fact]
    public void RetryLetterTopic_SupportedSubscriptionTypes_Should_Include_Shared_And_KeyShared()
    {
        RetryLetterTopic.SupportedSubscriptionTypes.ShouldContain(SubscriptionType.Shared);
        RetryLetterTopic.SupportedSubscriptionTypes.ShouldContain(SubscriptionType.KeyShared);
        RetryLetterTopic.SupportedSubscriptionTypes.ShouldNotContain(SubscriptionType.Exclusive);
        RetryLetterTopic.SupportedSubscriptionTypes.ShouldNotContain(SubscriptionType.Failover);
    }

    #endregion

    #region PulsarEnvelopeConstants Tests

    [Fact]
    public void PulsarEnvelopeConstants_Should_Have_Expected_Values()
    {
        PulsarEnvelopeConstants.ReconsumeTimes.ShouldBe("RECONSUMETIMES");
        PulsarEnvelopeConstants.DelayTimeMetadataKey.ShouldBe("DELAY_TIME");
        PulsarEnvelopeConstants.RealTopicMetadataKey.ShouldBe("REAL_TOPIC");
        PulsarEnvelopeConstants.OriginMessageIdMetadataKey.ShouldBe("ORIGIN_MESSAGE_ID");
        PulsarEnvelopeConstants.Exception.ShouldBe("EXCEPTION");
    }

    #endregion
}
