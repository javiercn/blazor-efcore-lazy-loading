using System.Collections;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlazorDbConcurrencyRepro.Data.Serialization;

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
