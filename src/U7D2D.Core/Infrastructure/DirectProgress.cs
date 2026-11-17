namespace U7D2D.Core.Infrastructure;

/// <summary>
/// Calls the handler on the reporting thread. Unlike <see cref="Progress{T}"/>, it does not post
/// every tick to a synchronization context, which matters when a folder scan reports per file.
/// </summary>
public sealed class DirectProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public DirectProgress(Action<T> report) => _report = report;

    public void Report(T value) => _report(value);
}
