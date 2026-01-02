using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlazorDbConcurrencyRepro.Data.Serialization;

/// <summary>
/// Wrapper for IAsyncEnumerable that acquires the semaphore BEFORE starting enumeration
/// and holds it until enumeration is complete. This is critical for ToListAsync scenarios.
/// </summary>
internal class ImmediatelyLockingAsyncEnumerable<T> : IAsyncEnumerable<T>
{
    private readonly IAsyncQueryProvider _provider;
    private readonly Expression _expression;
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationToken _cancellationToken;

    public ImmediatelyLockingAsyncEnumerable(
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

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken == default ? _cancellationToken : cancellationToken;
        return new ImmediatelyLockingAsyncEnumerator<T>(_provider, _expression, _semaphore, ct);
    }
}

/// <summary>
/// Async enumerator that acquires semaphore on first MoveNextAsync BEFORE the inner enumerable is created.
/// </summary>
internal class ImmediatelyLockingAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IAsyncQueryProvider _provider;
    private readonly Expression _expression;
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationToken _cancellationToken;
    private IAsyncEnumerator<T>? _innerEnumerator;
    private bool _lockHeld;
    private bool _disposed;

    public ImmediatelyLockingAsyncEnumerator(
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

        if (!hasMore)
        {
            // Release semaphore when enumeration completes
            ReleaseSemaphore();
        }

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

        if (_innerEnumerator != null)
        {
            await _innerEnumerator.DisposeAsync().ConfigureAwait(false);
        }

        ReleaseSemaphore();
    }
}
