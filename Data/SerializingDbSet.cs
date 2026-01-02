using System.Collections;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlazorDbConcurrencyRepro.Data;

/// <summary>
/// A DbSet wrapper that serializes async query operations using a SemaphoreSlim.
/// This allows concurrent calls to queue up and execute sequentially instead of throwing.
/// </summary>
public class SerializingDbSet<TEntity> : DbSet<TEntity>, IQueryable<TEntity>, IAsyncEnumerable<TEntity>
    where TEntity : class
{
    private readonly DbSet<TEntity> _innerSet;
    private readonly SemaphoreSlim _semaphore;
    private readonly SerializingQueryProvider _queryProvider;

    public SerializingDbSet(DbSet<TEntity> innerSet, SemaphoreSlim semaphore)
    {
        _innerSet = innerSet;
        _semaphore = semaphore;
        
        // Get the real query provider and wrap it
        var realProvider = ((IQueryable<TEntity>)_innerSet).Provider;
        _queryProvider = new SerializingQueryProvider(realProvider, semaphore);
    }

    // IQueryable implementation - this is key for LINQ operations
    Type IQueryable.ElementType => ((IQueryable<TEntity>)_innerSet).ElementType;
    Expression IQueryable.Expression => ((IQueryable<TEntity>)_innerSet).Expression;
    IQueryProvider IQueryable.Provider => _queryProvider;

    // IEnumerable implementation
    IEnumerator<TEntity> IEnumerable<TEntity>.GetEnumerator() => _innerSet.AsEnumerable().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_innerSet).GetEnumerator();

    // IAsyncEnumerable - for 'await foreach'
    public override IAsyncEnumerator<TEntity> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // Use a deferred enumerator that acquires semaphore before creating the inner enumerator
        return new DeferredLockingAsyncEnumerator<TEntity>(
            () => _innerSet.AsAsyncEnumerable().GetAsyncEnumerator(cancellationToken),
            _semaphore,
            cancellationToken);
    }

    // DbSet properties - delegate to inner
    public override IEntityType EntityType => _innerSet.EntityType;
    public override LocalView<TEntity> Local => _innerSet.Local;

    // Find operations - wrap with semaphore
    public override TEntity? Find(params object?[]? keyValues)
    {
        _semaphore.Wait();
        try
        {
            return _innerSet.Find(keyValues);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public override async ValueTask<TEntity?> FindAsync(params object?[]? keyValues)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _innerSet.FindAsync(keyValues).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public override async ValueTask<TEntity?> FindAsync(object?[]? keyValues, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _innerSet.FindAsync(keyValues, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // Entity state operations - these are typically quick, but let's be safe
    public override EntityEntry<TEntity> Add(TEntity entity) => _innerSet.Add(entity);
    public override async ValueTask<EntityEntry<TEntity>> AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        => await _innerSet.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public override EntityEntry<TEntity> Attach(TEntity entity) => _innerSet.Attach(entity);
    public override EntityEntry<TEntity> Remove(TEntity entity) => _innerSet.Remove(entity);
    public override EntityEntry<TEntity> Update(TEntity entity) => _innerSet.Update(entity);

    public override void AddRange(params TEntity[] entities) => _innerSet.AddRange(entities);
    public override void AddRange(IEnumerable<TEntity> entities) => _innerSet.AddRange(entities);
    public override async Task AddRangeAsync(params TEntity[] entities) => await _innerSet.AddRangeAsync(entities).ConfigureAwait(false);
    public override async Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        => await _innerSet.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);

    public override void AttachRange(params TEntity[] entities) => _innerSet.AttachRange(entities);
    public override void AttachRange(IEnumerable<TEntity> entities) => _innerSet.AttachRange(entities);
    public override void RemoveRange(params TEntity[] entities) => _innerSet.RemoveRange(entities);
    public override void RemoveRange(IEnumerable<TEntity> entities) => _innerSet.RemoveRange(entities);
    public override void UpdateRange(params TEntity[] entities) => _innerSet.UpdateRange(entities);
    public override void UpdateRange(IEnumerable<TEntity> entities) => _innerSet.UpdateRange(entities);

    public override EntityEntry<TEntity> Entry(TEntity entity) => _innerSet.Entry(entity);
}

/// <summary>
/// A query provider that wraps async execution in a SemaphoreSlim.
/// </summary>
internal class SerializingQueryProvider : IAsyncQueryProvider, IQueryProvider
{
    private readonly IQueryProvider _innerProvider;
    private readonly IAsyncQueryProvider? _innerAsyncProvider;
    private readonly SemaphoreSlim _semaphore;

    public SerializingQueryProvider(IQueryProvider innerProvider, SemaphoreSlim semaphore)
    {
        _innerProvider = innerProvider;
        _innerAsyncProvider = innerProvider as IAsyncQueryProvider;
        _semaphore = semaphore;
    }

    // IQueryProvider - for sync operations
    public IQueryable CreateQuery(Expression expression)
    {
        var query = _innerProvider.CreateQuery(expression);
        // Wrap in a serializing queryable to maintain the chain
        return new SerializingQueryable(query, _semaphore);
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        var query = _innerProvider.CreateQuery<TElement>(expression);
        
        // Check if this is an ordering operation or the result is ordered
        // OrderBy/ThenBy return IOrderedQueryable, so we need to check if the query implements it
        if (query is IOrderedQueryable<TElement> orderedQuery)
        {
            return new SerializingOrderedQueryable<TElement>(orderedQuery, _semaphore);
        }
        
        // Also check if the expression is an OrderBy/ThenBy call
        if (expression is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;
            if (methodName is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                // The result should be ordered - wrap as ordered even if runtime type check failed
                return new SerializingOrderedQueryable<TElement>((IOrderedQueryable<TElement>)query, _semaphore);
            }
        }
        
        return new SerializingQueryable<TElement>(query, _semaphore);
    }

    public object? Execute(Expression expression)
    {
        _semaphore.Wait();
        try
        {
            return _innerProvider.Execute(expression);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public TResult Execute<TResult>(Expression expression)
    {
        _semaphore.Wait();
        try
        {
            return _innerProvider.Execute<TResult>(expression);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // IAsyncQueryProvider - for async operations (ToListAsync, FirstAsync, etc.)
    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        if (_innerAsyncProvider == null)
        {
            throw new InvalidOperationException("The underlying provider does not support async operations.");
        }

        // TResult is typically Task<T> or IAsyncEnumerable<T>
        // We need to wrap the execution
        
        var resultType = typeof(TResult);
        
        // Check if it's an IAsyncEnumerable<T>
        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            // For IAsyncEnumerable, we need to acquire the semaphore BEFORE starting enumeration
            // and hold it until enumeration is complete
            var elementType = resultType.GetGenericArguments()[0];
            
            // Create a wrapper that acquires semaphore immediately and holds through enumeration
            var wrapperType = typeof(ImmediatelyLockingAsyncEnumerable<>).MakeGenericType(elementType);
            var wrapper = Activator.CreateInstance(wrapperType, _innerAsyncProvider, expression, _semaphore, cancellationToken);
            return (TResult)wrapper!;
        }
        
        // For Task<T> results (FirstAsync, CountAsync, etc.), wrap in semaphore
        return WrapAsyncExecution<TResult>(expression, cancellationToken);
    }

    private TResult WrapAsyncExecution<TResult>(Expression expression, CancellationToken cancellationToken)
    {
        // TResult is Task<Something> or ValueTask<Something>
        var resultType = typeof(TResult);
        
        if (resultType.IsGenericType)
        {
            var genericDef = resultType.GetGenericTypeDefinition();
            
            if (genericDef == typeof(Task<>))
            {
                var innerType = resultType.GetGenericArguments()[0];
                var method = typeof(SerializingQueryProvider)
                    .GetMethod(nameof(WrapTaskAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(innerType);
                return (TResult)method.Invoke(this, [expression, cancellationToken])!;
            }
        }
        
        // Fallback - just execute directly (shouldn't happen in practice)
        return _innerAsyncProvider!.ExecuteAsync<TResult>(expression, cancellationToken);
    }

    private async Task<T> WrapTaskAsync<T>(Expression expression, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _innerAsyncProvider!.ExecuteAsync<Task<T>>(expression, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

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
