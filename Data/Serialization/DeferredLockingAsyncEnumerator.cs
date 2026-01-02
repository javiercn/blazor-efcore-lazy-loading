namespace BlazorDbConcurrencyRepro.Data.Serialization;

/// <summary>
/// Async enumerator that acquires semaphore on first MoveNextAsync BEFORE creating the inner enumerator.
/// Used for DbSet.GetAsyncEnumerator() where we need to defer inner enumerator creation.
/// </summary>
internal class DeferredLockingAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly Func<IAsyncEnumerator<T>> _innerFactory;
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationToken _cancellationToken;
    private IAsyncEnumerator<T>? _innerEnumerator;
    private bool _lockHeld;
    private bool _disposed;

    public DeferredLockingAsyncEnumerator(
        Func<IAsyncEnumerator<T>> innerFactory,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        _innerFactory = innerFactory;
        _semaphore = semaphore;
        _cancellationToken = cancellationToken;
    }

    public T Current => _innerEnumerator is not null ? _innerEnumerator.Current : throw new InvalidOperationException("Enumeration not started");

    public async ValueTask<bool> MoveNextAsync()
    {
        if (_disposed)
            return false;

        // Acquire semaphore on FIRST call, before creating the inner enumerator
        if (!_lockHeld)
        {
            await _semaphore.WaitAsync(_cancellationToken).ConfigureAwait(false);
            _lockHeld = true;

            // NOW create the inner enumerator while we hold the semaphore
            _innerEnumerator = _innerFactory();
        }

        return await _innerEnumerator!.MoveNextAsync().ConfigureAwait(false);
    }

    private void ReleaseSemaphore()
    {
        if (_lockHeld)
        {
            _semaphore.Release();
            _lockHeld = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_innerEnumerator != null)
        {
            await _innerEnumerator.DisposeAsync().ConfigureAwait(false);
        }

        ReleaseSemaphore();
    }
}
