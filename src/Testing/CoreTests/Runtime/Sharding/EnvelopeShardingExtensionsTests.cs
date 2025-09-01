using Wolverine.ComplianceTests;
using Wolverine.Runtime.Sharding;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Runtime.Sharding;

public class EnvelopeShardingExtensionsTests
{
    private readonly ITestOutputHelper _output;
    private readonly MessagePartitioningRules theRules;

    public EnvelopeShardingExtensionsTests(ITestOutputHelper output)
    {
        _output = output;
        theRules = new MessagePartitioningRules(new());
        theRules.ByMessage<ICoffee>(x => x.Name);
    }

    [Fact]
    public void slot_for_processing_is_consistent()
    {
        var ids = new string[]
        {
            "78dd3ab8-9eb4-4eb5-847a-24d6b260e176", 
            "400b7814-00c6-448c-bd6f-a10f271ba68d", 
            "b1562605-180d-429d-a109-123519b5a8f8",
            "4ad3fb5e-bffc-4f86-9ba6-e72fce936495",
            "20e34ef4-7231-489a-a730-601b43b3a7d8"
        };

        var expected = new int[] { 4, 1, 1, 2, 3 };
        
        for (int i = 0; i < ids.Length; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Message = new Coffee2(ids[i]);
            var slot = envelope.SlotForProcessing(5, theRules);
            slot.ShouldBe(expected[i]);
        }
   
    }
    
    
    [Fact]
    public void slot_for_sending_is_consistent()
    {
        var ids = new string[]
        {
            "78dd3ab8-9eb4-4eb5-847a-24d6b260e176", 
            "400b7814-00c6-448c-bd6f-a10f271ba68d", 
            "b1562605-180d-429d-a109-123519b5a8f8",
            "4ad3fb5e-bffc-4f86-9ba6-e72fce936495",
            "20e34ef4-7231-489a-a730-601b43b3a7d8"
        };

        var expected = new int[] { 3, 2, 3, 1, 4 };
        
        for (int i = 0; i < ids.Length; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Message = new Coffee2(ids[i]);
            var slot = envelope.SlotForSending(5, theRules);
            _output.WriteLine($"{i} was {slot}");
            
            slot.ShouldBe(expected[i]);
        }
   
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public void valid_number_of_processing_slots_happy_path(int slotCount)
    {
        slotCount.AssertIsValidNumberOfProcessingSlots();
    }
    
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void invalid_number_of_processing_slots_happy_path(int slotCount)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            slotCount.AssertIsValidNumberOfProcessingSlots();
        });
    }
}