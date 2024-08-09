using Microsoft.Extensions.Hosting;

namespace Wolverine.ComplianceTests.Sagas;

public interface ISagaHost
{
    IHost BuildHost<TSaga>();

    Task<T> LoadState<T>(Guid id) where T : Saga;
    Task<T> LoadState<T>(int id) where T : Saga;
    Task<T> LoadState<T>(long id) where T : Saga;
    Task<T> LoadState<T>(string id) where T : Saga;
}