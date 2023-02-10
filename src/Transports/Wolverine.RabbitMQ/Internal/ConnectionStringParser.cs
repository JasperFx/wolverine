using JasperFx.Core;
using Microsoft.CodeAnalysis;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

internal static class ConnectionStringParser
{
    public static void Apply(string connectionString, ConnectionFactory factory)
    {
        var values = connectionString.ToDelimitedArray(';');
        foreach (var value in values)
        {
            var parts = value.ToDelimitedArray('=');
            if (parts.Length != 2)
            {
                throw new ArgumentOutOfRangeException(
                    "Invalid connection string syntax. Use key1=value1;key2=value2 syntax");
            }

            Parse(parts[0].ToLower(), parts[1], factory);
        }
    }

    internal static void Parse(string key, string value, ConnectionFactory factory)
    {
        switch (key)
        {
            case "host":
                factory.HostName = value;
                break;
            
            case "port":
                if (int.TryParse(value, out var port))
                {
                    factory.Port = port;
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"Supplied port '{value}' is an invalid number");
                }

                break;
            
            case "username":
                factory.UserName = value;
                break;
            
            case "password":
                factory.Password = value;
                break;
            
            case "usetls":
                Console.WriteLine("Wolverine does not respect the UseTLS flag, you will need to configure that directly on ConnectionFactory");
                break;
        }
    }
}