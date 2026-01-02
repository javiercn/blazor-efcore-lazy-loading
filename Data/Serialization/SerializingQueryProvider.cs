using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlazorDbConcurrencyRepro.Data.Serialization;

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
