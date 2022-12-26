using Microsoft.Extensions.Hosting;

namespace TestingSupport;

public abstract class BaseContext : IDisposable
{
    private readonly bool _shouldStart;
    protected readonly IHostBuilder builder = Host.CreateDefaultBuilder();


    private IHost _host;

    protected BaseContext(bool shouldStart)
    {
        _shouldStart = shouldStart;
    }

    public IHost theHost
    {
        get
        {
            if (_host == null)
            {
                _host = builder.Build();
                if (_shouldStart)
                {
                    _host.Start();
                }
            }

            return _host;
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
    }
}