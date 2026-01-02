using System.Collections;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlazorDbConcurrencyRepro.Data.Serialization;

/// <summary>
/// Wrapper for IQueryable that maintains the serializing provider through the chain.
/// </summary>
internal class SerializingQueryable : IQueryable
{
    protected readonly IQueryable _inner;
    protected readonly SemaphoreSlim _semaphore;
    protected readonly SerializingQueryProvider _provider;

    public SerializingQueryable(IQueryable inner, SemaphoreSlim semaphore)
    {
        _inner = inner;
        _semaphore = semaphore;
        _provider = new SerializingQueryProvider(inner.Provider, semaphore);
    }

    public Type ElementType => _inner.ElementType;
    public Expression Expression => _inner.Expression;
    public IQueryProvider Provider => _provider;

    public IEnumerator GetEnumerator() => _inner.GetEnumerator();
}

/// <summary>
/// Generic wrapper for IQueryable&lt;T&gt;.
/// </summary>
internal class SerializingQueryable<T> : SerializingQueryable, IQueryable<T>, IAsyncEnumerable<T>, IOrderedQueryable<T>
{
    public SerializingQueryable(IQueryable<T> inner, SemaphoreSlim semaphore) 
        : base(inner, semaphore)
    {
    }

    public new IEnumerator<T> GetEnumerator() => ((IQueryable<T>)_inner).GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // Use immediate locking - acquire semaphore BEFORE getting inner enumerator
        // This is critical for ToListAsync() scenarios
        return new ImmediatelyLockingQueryableEnumerator<T>(
            _inner.Provider as IAsyncQueryProvider ?? throw new InvalidOperationException("Provider must support IAsyncQueryProvider"),
            _inner.Expression,
            _semaphore,
            cancellationToken);
    }
}

/// <summary>
/// Wrapper for IOrderedQueryable&lt;T&gt; to preserve ordering operations.
/// </summary>
internal class SerializingOrderedQueryable<T> : SerializingQueryable<T>, IOrderedQueryable<T>
{
    public SerializingOrderedQueryable(IOrderedQueryable<T> inner, SemaphoreSlim semaphore) 
        : base(inner, semaphore)
    {
    }
}
