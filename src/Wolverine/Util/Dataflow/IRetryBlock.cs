namespace Wolverine.Util.Dataflow;

/// <summary>
/// Abstract a way we can retry on <typeparamref name="T"/> message
/// </summary>
/// <typeparam name="T"></typeparam>
internal interface IRetryBlock<T> : IDisposable
{
    /// <summary>
    /// Send <typeparamref name="T"/> message in async manner
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    Task PostAsync(T message);
}
