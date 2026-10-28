using System.Text;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

using CoreTests.Transports.NServiceBusXml;

namespace CoreTests.Transports
{

    /// <summary>
    ///     GH-5244. NServiceBus defaulted to its XmlSerializer for years, so plenty of NServiceBus systems still put
    ///     XML on the wire. The payloads below were captured from NServiceBus 10.2.6's XmlSerializer (through its
    ///     learning transport) and are kept here as plain strings -- no NServiceBus binaries belong in this
    ///     repository. The end to end proof against live NServiceBus endpoints lives in the wolverine-interop suite.
    /// </summary>
    public class nservicebus_xml_serialization_5244
    {
        private const string NServiceBusNamespace = "Sample.Messages";

        // NServiceBus 10.2.6, UseSerialization<XmlSerializer>(), for an instance of the Everything class below
        private const string EverythingXml =
            "<?xml version=\"1.0\"?><Everything xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns=\"http://tempuri.net/Sample.Messages\"><Name>Hello &lt;world&gt; &amp; \"friends\"</Name><Count>42</Count><Big>123456789012</Big><Amount>123.45</Amount><Ratio>1.5</Ratio><Flag>true</Flag><Id>c3c5a2fa-1111-4a0e-9a3c-0123456789ab</Id><When>2026-10-28T13:14:15.123Z</When><WhenLocal>2026-10-28T13:14:15</WhenLocal><WhenOffset>2026-10-28T13:14:15-05:00</WhenOffset><Duration>PT1H30M</Duration><Color>Green</Color><MaybeCount xsi:nil=\"true\"></MaybeCount><MaybeCount2>7</MaybeCount2><Tags><String>a</String><String>b</String></Tags><Numbers><Int32>1</Int32><Int32>2</Int32><Int32>3</Int32></Numbers><Child><Name>kid</Name><Age>3</Age></Child><Children><Child><Name>c1</Name><Age>1</Age></Child><Child><Name>c2</Name><Age>2</Age></Child></Children><Lookup><NServiceBus.KeyValuePairOfStringAndInt32><Key>x</Key><Value>1</Value></NServiceBus.KeyValuePairOfStringAndInt32><NServiceBus.KeyValuePairOfStringAndInt32><Key>y</Key><Value>2</Value></NServiceBus.KeyValuePairOfStringAndInt32></Lookup><Bytes>AQID</Bytes></Everything>";

        // Public fields come after the properties, flags enums are comma separated, and a type from another
        // namespace (Other.Thing) does not get a namespace of its own
        private const string MoreXml =
            "<?xml version=\"1.0\"?><More xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns=\"http://tempuri.net/Sample.Messages\"><Ids><Guid>00000000-0000-0000-0000-000000000000</Guid></Ids><MaybeWhen>2026-01-02T00:00:00Z</MaybeWhen><Letter>z</Letter><Flags>Read, Write</Flags><Set><String>s</String></Set><Other><Value>5</Value></Other><Things><Thing><Value>6</Value></Thing></Things><Floaty>0.1</Floaty><Uri>http://x/y</Uri><Field>f</Field></More>";

        // NServiceBus' own proxy for Send<IInterfaceMessage>(x => ...): the root element is the interface name
        private const string InterfaceXml =
            "<?xml version=\"1.0\"?><IInterfaceMessage xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns=\"http://tempuri.net/Sample.Messages\"><Id>c3c5a2fa-2222-4a0e-9a3c-0123456789ab</Id><Name>iface</Name></IInterfaceMessage>";

        // A derived message carries its base types as xmlns:baseType declarations, derived members first
        private const string DerivedXml =
            "<?xml version=\"1.0\"?><Derived xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:baseType=\"Sample.Messages.Base\" xmlns=\"http://tempuri.net/Sample.Messages\"><DerivedValue>d</DerivedValue><BaseValue>b</BaseValue></Derived>";

        private const string EmptyXml =
            "<?xml version=\"1.0\"?><Empty xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns=\"http://tempuri.net/Sample.Messages\"></Empty>";

        private readonly NServiceBusXmlSerializer theSerializer = new();

        private static Everything everything() => new()
        {
            Name = "Hello <world> & \"friends\"",
            Count = 42,
            Big = 123456789012L,
            Amount = 123.45m,
            Ratio = 1.5,
            Flag = true,
            Id = Guid.Parse("c3c5a2fa-1111-4a0e-9a3c-0123456789ab"),
            When = new DateTime(2026, 10, 28, 13, 14, 15, 123, DateTimeKind.Utc),
            WhenLocal = new DateTime(2026, 10, 28, 13, 14, 15, DateTimeKind.Unspecified),
            WhenOffset = new DateTimeOffset(2026, 10, 28, 13, 14, 15, TimeSpan.FromHours(-5)),
            Duration = TimeSpan.FromMinutes(90),
            Color = Color.Green,
            MaybeCount = null,
            MaybeCount2 = 7,
            Tags = ["a", "b"],
            Numbers = [1, 2, 3],
            Child = new Child { Name = "kid", Age = 3 },
            Children = [new Child { Name = "c1", Age = 1 }, new Child { Name = "c2", Age = 2 }],
            Lookup = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 },
            Nothing = null,
            Bytes = [1, 2, 3]
        };

        private T read<T>(string xml)
        {
            var envelope = new Envelope { Data = Encoding.UTF8.GetBytes(xml) };
            return theSerializer.ReadFromData(typeof(T), envelope).ShouldBeOfType<T>();
        }

        [Fact]
        public void content_type_is_what_nservicebus_stamps_for_xml()
        {
            theSerializer.ContentType.ShouldBe("text/xml");
        }

        [Fact]
        public void writes_the_same_bytes_nservicebus_does()
        {
            Encoding.UTF8.GetString(theSerializer.WriteMessage(everything()))
                .ShouldBe(EverythingXml.Replace(NServiceBusNamespace, typeof(Everything).Namespace));
        }

        [Fact]
        public void writes_no_byte_order_mark()
        {
            theSerializer.WriteMessage(new Empty())[0].ShouldBe((byte)'<');
        }

        [Fact]
        public void writes_an_empty_message_like_nservicebus()
        {
            Encoding.UTF8.GetString(theSerializer.WriteMessage(new Empty()))
                .ShouldBe(EmptyXml.Replace(NServiceBusNamespace, typeof(Empty).Namespace));
        }

        [Fact]
        public void honors_a_custom_namespace()
        {
            var serializer = new NServiceBusXmlSerializer { Namespace = "urn:acme" };
            Encoding.UTF8.GetString(serializer.WriteMessage(new Empty()))
                .ShouldContain($"xmlns=\"urn:acme/{typeof(Empty).Namespace}\"");
        }

        [Fact]
        public void reads_everything_nservicebus_wrote()
        {
            var message = read<Everything>(EverythingXml);

            message.ShouldBeEquivalentTo(everything());
        }

        [Fact]
        public void reads_fields_flags_and_other_namespaces()
        {
            var message = read<More>(MoreXml);

            message.Field.ShouldBe("f");
            message.Ids.ShouldBe([Guid.Empty]);
            message.MaybeWhen.ShouldBe(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            message.Letter.ShouldBe('z');
            message.Flags.ShouldBe(Perms.Read | Perms.Write);
            message.Set.ShouldBe(["s"]);
            message.Other.Value.ShouldBe(5);
            message.Things.Single().Value.ShouldBe(6);
            message.Floaty.ShouldBe(0.1f);
            message.Uri.ShouldBe(new Uri("http://x/y"));
        }

        [Fact]
        public void reads_an_interface_message_into_the_concrete_type_wolverine_maps_it_to()
        {
            // The root element names the interface; Wolverine has already picked the concrete type from the
            // NServiceBus.EnclosedMessageTypes header through RegisterInteropMessageAssembly
            var message = read<ConcreteMessage>(InterfaceXml);

            message.Id.ShouldBe(Guid.Parse("c3c5a2fa-2222-4a0e-9a3c-0123456789ab"));
            message.Name.ShouldBe("iface");
        }

        [Fact]
        public void reads_inherited_members()
        {
            var message = read<Derived>(DerivedXml);

            message.BaseValue.ShouldBe("b");
            message.DerivedValue.ShouldBe("d");
        }

        [Fact]
        public void reads_an_empty_message()
        {
            read<Empty>(EmptyXml).ShouldNotBeNull();
        }

        [Fact]
        public void reads_the_first_message_out_of_a_legacy_messages_wrapper()
        {
            var xml =
                "<?xml version=\"1.0\"?><Messages xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://tempuri.net/Sample.Messages\"><Derived><DerivedValue>d</DerivedValue><BaseValue>b</BaseValue></Derived></Messages>";

            read<Derived>(xml).DerivedValue.ShouldBe("d");
        }

        [Fact]
        public void ignores_elements_the_receiving_type_does_not_have()
        {
            var xml = EmptyXml.Replace("></Empty>", "><Unknown>1</Unknown></Empty>");
            read<Empty>(xml).ShouldNotBeNull();
        }

        [Fact]
        public void reads_into_records_and_interface_typed_collections()
        {
            var xml =
                "<?xml version=\"1.0\"?><NsbRecord xmlns=\"http://tempuri.net/x\"><Name>r</Name><Values><Int32>1</Int32><Int32>2</Int32></Values><Lookup><NServiceBus.KeyValuePairOfStringAndString><Key>k</Key><Value>v</Value></NServiceBus.KeyValuePairOfStringAndString></Lookup></NsbRecord>";

            var message = read<NsbRecord>(xml);

            message.Name.ShouldBe("r");
            message.Values.ShouldBe([1, 2]);
            message.Lookup["k"].ShouldBe("v");
        }

        [Fact]
        public void round_trips()
        {
            var bytes = theSerializer.WriteMessage(everything());
            var envelope = new Envelope { Data = bytes };

            theSerializer.ReadFromData(typeof(Everything), envelope).ShouldBeEquivalentTo(everything());
        }

        [Fact]
        public void refuses_to_read_into_an_interface_with_a_pointer_to_the_fix()
        {
            var envelope = new Envelope { Data = Encoding.UTF8.GetBytes(InterfaceXml) };

            Should.Throw<InvalidOperationException>(() => theSerializer.ReadFromData(typeof(IInterfaceMessage), envelope))
                .Message.ShouldContain("RegisterInteropMessageAssembly");
        }

        [Fact]
        public void interop_endpoint_reads_xml_and_json_and_sends_json_by_default()
        {
            var endpoint = new LocalQueue("nsb");
            var json = new SystemTextJsonSerializer(new());

            NServiceBusInterop.UseNServiceBusSerializers(endpoint, json);

            endpoint.DefaultSerializer.ShouldBeSameAs(json);
            endpoint.TryFindSerializer("application/json").ShouldBeSameAs(json);
            endpoint.TryFindSerializer("text/xml").ShouldBeOfType<NServiceBusXmlSerializer>();
        }

        [Fact]
        public void interop_endpoint_keeps_xml_as_the_default_when_it_was_chosen_first()
        {
            var endpoint = new LocalQueue("nsb");
            var xml = new NServiceBusXmlSerializer { Namespace = "urn:acme" };
            var json = new SystemTextJsonSerializer(new());
            endpoint.DefaultSerializer = xml;

            NServiceBusInterop.UseNServiceBusSerializers(endpoint, json);

            endpoint.DefaultSerializer.ShouldBeSameAs(xml);
            endpoint.TryFindSerializer("text/xml").ShouldBeSameAs(xml);
            endpoint.TryFindSerializer("application/json").ShouldBeSameAs(json);
        }

        [Fact]
        public void interop_endpoint_takes_xml_as_the_default_when_it_is_chosen_afterward()
        {
            var endpoint = new LocalQueue("nsb");
            var json = new SystemTextJsonSerializer(new());
            NServiceBusInterop.UseNServiceBusSerializers(endpoint, json);

            var xml = new NServiceBusXmlSerializer();
            endpoint.DefaultSerializer = xml;

            endpoint.DefaultSerializer.ShouldBeSameAs(xml);
            endpoint.TryFindSerializer("text/xml").ShouldBeSameAs(xml);
            endpoint.TryFindSerializer("application/json").ShouldBeSameAs(json);
        }
    }
}

namespace CoreTests.Transports.NServiceBusXml
{
    public enum Color
    {
        Red,
        Green
    }

    [Flags]
    public enum Perms
    {
        None = 0,
        Read = 1,
        Write = 2
    }

    public class Child
    {
        public string Name { get; set; } = null!;
        public int Age { get; set; }
    }

    public class Everything
    {
        public string Name { get; set; } = null!;
        public int Count { get; set; }
        public long Big { get; set; }
        public decimal Amount { get; set; }
        public double Ratio { get; set; }
        public bool Flag { get; set; }
        public Guid Id { get; set; }
        public DateTime When { get; set; }
        public DateTime WhenLocal { get; set; }
        public DateTimeOffset WhenOffset { get; set; }
        public TimeSpan Duration { get; set; }
        public Color Color { get; set; }
        public int? MaybeCount { get; set; }
        public int? MaybeCount2 { get; set; }
        public List<string> Tags { get; set; } = null!;
        public int[] Numbers { get; set; } = null!;
        public Child Child { get; set; } = null!;
        public List<Child> Children { get; set; } = null!;
        public Dictionary<string, int> Lookup { get; set; } = null!;
        public string? Nothing { get; set; }
        public byte[] Bytes { get; set; } = null!;
    }

    public class Thing
    {
        public int Value { get; set; }
    }

    public class More
    {
        public string Field = null!;
        public List<Guid> Ids { get; set; } = null!;
        public DateTime? MaybeWhen { get; set; }
        public char Letter { get; set; }
        public Perms Flags { get; set; }
        public HashSet<string> Set { get; set; } = null!;
        public Thing Other { get; set; } = null!;
        public List<Thing> Things { get; set; } = null!;
        public float Floaty { get; set; }
        public Uri Uri { get; set; } = null!;
    }

    public interface IInterfaceMessage
    {
        Guid Id { get; set; }
        string Name { get; set; }
    }

    public class ConcreteMessage : IInterfaceMessage
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
    }

    public class Base
    {
        public string BaseValue { get; set; } = null!;
    }

    public class Derived : Base
    {
        public string DerivedValue { get; set; } = null!;
    }

    public class Empty;

    public record NsbRecord(string Name, IReadOnlyList<int> Values, IDictionary<string, string> Lookup);
}
