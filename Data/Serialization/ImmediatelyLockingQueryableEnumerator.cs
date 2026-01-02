using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlazorDbConcurrencyRepro.Data.Serialization;

/// <summary>
/// Async enumerator for queryables that acquires semaphore on first MoveNextAsync BEFORE the inner enumerable is created.
/// </summary>
internal class ImmediatelyLockingQueryableEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IAsyncQueryProvider _provider;
    private readonly Expression _expression;
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationToken _cancellationToken;
    private IAsyncEnumerator<T>? _innerEnumerator;
    private bool _lockHeld;
    private bool _disposed;

    public ImmediatelyLockingQueryableEnumerator(
        IAsyncQueryProvider provider,
        Expression expression,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        _provider = provider;
        _expression = expression;
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

            // NOW create the inner enumerable and enumerator while we hold the semaphore
            var innerEnumerable = _provider.ExecuteAsync<IAsyncEnumerable<T>>(_expression, _cancellationToken);
            _innerEnumerator = innerEnumerable.GetAsyncEnumerator(_cancellationToken);
        }

        var hasMore = await _innerEnumerator!.MoveNextAsync().ConfigureAwait(false);

        // Don't release semaphore here - wait for DisposeAsync to ensure DataReader is closed
        // The await foreach pattern guarantees DisposeAsync is called

        return hasMore;
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

        // First dispose the inner enumerator (this closes the DataReader)
        if (_innerEnumerator != null)
        {
            await _innerEnumerator.DisposeAsync().ConfigureAwait(false);
        }

        // Then release the semaphore
        ReleaseSemaphore();
    }
}
