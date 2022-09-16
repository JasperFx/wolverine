using System.Threading.Tasks;
using Wolverine;
using Wolverine.Transports.Tcp;
using Microsoft.Extensions.Hosting;
using Oakton;

namespace Publisher
{
    internal class Program
    {
        public static Task<int> Main(string[] args)
        {
            return Host
                .CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ListenAtPort(2211);
                })
                .RunOaktonCommands(args);
        }
    }



}
