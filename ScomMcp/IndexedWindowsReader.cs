namespace ScomMcp;

public sealed class IndexedWindowsReader(ReadModelStore store) : ISnapshotReader
{
    public Task<Dataset> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(store.ReadWindowsDataset());
    }
}
