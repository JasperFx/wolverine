using System;
using System.Threading.Tasks;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Serialization;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Shouldly;
using Xunit;

namespace DocumentationSamples
{
    namespace FirstTry
    {
        #region sample_PersonBorn1

        public class PersonBorn
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }

            // This is obviously a contrived example
            // so just let this go for now;)
            public int Day { get; set; }
            public int Month { get; set; }
            public int Year { get; set; }
        }

        #endregion


        public class message_alias
        {
            #region sample_ootb_message_alias

            [Fact]
            public void message_alias_is_fullname_by_default()
            {
                new Envelope(new PersonBorn())
                    .MessageType.ShouldBe(typeof(PersonBorn).FullName);
            }

            #endregion
        }
    }

    namespace SecondTry
    {
        #region sample_override_message_alias

        [MessageIdentity("person-born")]
        public class PersonBorn
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public int Day { get; set; }
            public int Month { get; set; }
            public int Year { get; set; }
        }

        #endregion

        public class message_alias
        {
            #region sample_explicit_message_alias

            [Fact]
            public void message_alias_is_fullname_by_default()
            {
                new Envelope(new PersonBorn())
                    .MessageType.ShouldBe("person-born");
            }

            #endregion
        }
    }

    namespace ThirdTry
    {
        #region sample_PersonBorn_V2

        [MessageIdentity("person-born", Version = 2)]
        public class PersonBornV2
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public DateTime Birthday { get; set; }
        }

        #endregion

        #region sample_IForwardsTo_PersonBornV2

        public class PersonBorn : IForwardsTo<PersonBornV2>
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public int Day { get; set; }
            public int Month { get; set; }
            public int Year { get; set; }

            public PersonBornV2 Transform()
            {
                return new PersonBornV2
                {
                    FirstName = FirstName,
                    LastName = LastName,
                    Birthday = new DateTime(Year, Month, Day)
                };
            }
        }

        #endregion

        #region sample_PersonCreatedHandler

        public class PersonCreatedHandler
        {
            public static void Handle(PersonBorn person)
            {
                // do something w/ the message
            }

            public static void Handle(PersonBornV2 person)
            {
                // do something w/ the message
            }
        }

        #endregion
    }


    public class MyCustomWriter : IMessageSerializer
    {
        public Type DotNetType { get; }
        public string? ContentType { get; }

        public byte[] Write(Envelope model)
        {
            throw new NotImplementedException();
        }

        public object ReadFromData(Type messageType, Envelope envelope)
        {
            throw new NotImplementedException();
        }

        public object? ReadFromData(byte[]? data)
        {
            throw new NotImplementedException();
        }

        public byte[] WriteMessage(object message)
        {
            throw new System.NotImplementedException();
        }
    }

    public static class MessageVersioning
    {
        public static async Task CustomizingJsonSerialization()
        {
            #region sample_CustomizingJsonSerialization

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseNewtonsoftForSerialization(settings =>
                    {
                        settings.ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor;
                    });
                }).StartAsync();

            #endregion
        }
    }
}
