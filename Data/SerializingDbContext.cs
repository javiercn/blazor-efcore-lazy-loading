using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace BlazorDbConcurrencyRepro.Data;

/// <summary>
/// A DbContext base class that automatically serializes all operations using a SemaphoreSlim.
/// Derived classes don't need any special code - all DbSet access and SaveChanges operations
/// are automatically serialized.
/// 
/// This class implements ISerializingDbContext to expose its semaphore to SerializingLazyLoader,
/// ensuring that lazy loading operations are also serialized.
/// 
/// Usage:
/// 1. Inherit from SerializingDbContext instead of DbContext
/// 2. Use Set&lt;TEntity&gt;() or DbSet properties as normal - they automatically serialize
/// 3. For lazy loading support, use ReplaceService&lt;ILazyLoader, SerializingLazyLoader&gt;()
/// </summary>
public abstract class SerializingDbContext : DbContext, ISerializingDbContext
{
    // Each DbContext instance has its own semaphore (1 concurrent operation)
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private bool _disposed;
    
    // Cache of wrapped DbSets to ensure same instance is returned for same entity type
    private readonly ConcurrentDictionary<Type, object> _wrappedSets = new();
    private readonly ConcurrentDictionary<(Type, string), object> _wrappedSharedSets = new();

    protected SerializingDbContext()
    {
    }

    protected SerializingDbContext(DbContextOptions options) : base(options)
    {
    }

    /// <summary>
    /// Gets the semaphore used to serialize operations on this DbContext.
    /// </summary>
    public SemaphoreSlim OperationSemaphore => _operationSemaphore;

    public override DbSet<TEntity> Set<TEntity>()
    {
        return (DbSet<TEntity>)_wrappedSets.GetOrAdd(
            typeof(TEntity),
            _ => new SerializingDbSet<TEntity>(base.Set<TEntity>(), _operationSemaphore));
    }

    public override DbSet<TEntity> Set<TEntity>(string name)
    {
        return (DbSet<TEntity>)_wrappedSharedSets.GetOrAdd(
            (typeof(TEntity), name),
            _ => new SerializingDbSet<TEntity>(base.Set<TEntity>(name), _operationSemaphore));
    }

    #region SaveChanges overrides

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public override int SaveChanges()
    {
        _operationSemaphore.Wait();
        try
        {
            return base.SaveChanges();
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        _operationSemaphore.Wait();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    #endregion

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _operationSemaphore.Dispose();
        }
        base.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _operationSemaphore.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
