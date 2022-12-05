using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Oakton;
using Wolverine;
using Wolverine.Transports.Tcp;

namespace Publisher
{
    internal class Program
    {
        public static Task<int> Main(string[] args)
        {
            return Host
                .CreateDefaultBuilder()
                .UseWolverine(opts => { opts.ListenAtPort(2211); })
                .RunOaktonCommands(args);
        }
    }
}