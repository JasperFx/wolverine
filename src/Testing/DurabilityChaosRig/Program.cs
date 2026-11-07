using DurabilityChaosRig;

// driver [chaosSeconds] [workers] [seed]  -- runs a whole chaos round and checks the ledger
// worker / producer                       -- the child processes the driver starts
// report                                  -- re-reads the ledger of the last run
// The scenario comes from the environment; see README.md
var role = args.Length > 0 ? args[0] : "driver";

switch (role)
{
    case "worker":
        await Node.RunWorkerAsync();
        return 0;

    case "producer":
        await Node.RunProducerAsync();
        return 0;

    case "report":
        Console.WriteLine(await Report.ReadAsync());
        return 0;

    default:
        var seconds = args.Length > 1 ? int.Parse(args[1]) : 120;
        var workers = args.Length > 2 ? int.Parse(args[2]) : 3;
        var seed = args.Length > 3 ? int.Parse(args[3]) : Random.Shared.Next();
        var dir = Environment.GetEnvironmentVariable("CHAOS_DIR") ??
                  Path.Combine(Path.GetTempPath(), "durability-chaos", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Environment.SetEnvironmentVariable("CHAOS_DIR", dir);

        return await new Driver(dir, workers, TimeSpan.FromSeconds(seconds), seed).RunAsync();
}
