using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlazorDbConcurrencyRepro.Data.Serialization;

/// <summary>
/// A custom ILazyLoader that wraps lazy loading calls with a semaphore to serialize
/// operations on the same DbContext. This is critical for Blazor Server where
/// multiple components may trigger lazy loading concurrently.
/// 
/// This loader gets the semaphore from the SerializingDbContext,
/// ensuring it uses the same semaphore for query serialization.
/// 
/// Since lazy loading is inherently synchronous (property getter triggers Load()),
/// we use synchronous Wait() on the semaphore - blocking is acceptable here.
/// </summary>
public class SerializingLazyLoader : ILazyLoader, IInjectableService
{
    private readonly ICurrentDbContext _currentContext;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Infrastructure> _logger;
    
    private bool _disposed;
    private bool _detached;
    private Dictionary<string, bool>? _loadedStates;

    public SerializingLazyLoader(
        ICurrentDbContext currentContext,
        IDiagnosticsLogger<DbLoggerCategory.Infrastructure> logger)
    {
        _currentContext = currentContext;
        _logger = logger;
    }

    protected DbContext? Context => _currentContext?.Context;

    /// <summary>
    /// Gets the semaphore from the DbContext if it's a SerializingDbContext.
    /// Returns null if the DbContext doesn't support serialization.
    /// </summary>
    private SemaphoreSlim? GetSemaphore() 
        => (Context as SerializingDbContext)?.OperationSemaphore;

    /// <summary>
    /// Called by EF Core when injecting the lazy loader into an entity.
    /// </summary>
    public void Injected(DbContext context, object entity, QueryTrackingBehavior? queryTrackingBehavior, ITypeBase structuralType)
    {
        // The context is already available via ICurrentDbContext
    }

    /// <summary>
    /// Called when an entity is being attached to the context.
    /// </summary>
    public void Attaching(DbContext context, IEntityType entityType, object entity)
    {
        // Nothing special needed when attaching
    }

    public void SetLoaded(object entity, [CallerMemberName] string navigationName = "", bool loaded = true)
    {
        _loadedStates ??= new Dictionary<string, bool>();
        _loadedStates[navigationName] = loaded;
    }

    public bool IsLoaded(object entity, [CallerMemberName] string navigationName = "")
    {
        return _loadedStates != null 
               && _loadedStates.TryGetValue(navigationName, out var loaded) 
               && loaded;
    }

    /// <summary>
    /// Synchronous lazy load - acquires semaphore synchronously.
    /// This is safe because all async operations use ConfigureAwait(false),
    /// so their continuations don't need the sync context and can complete
    /// on thread pool threads even if we block here.
    /// </summary>
    public void Load(object entity, [CallerMemberName] string navigationName = "")
    {
        if (_disposed || _detached || Context == null)
            return;

        if (IsLoaded(entity, navigationName))
            return;

        var semaphore = GetSemaphore();
        
        // If no semaphore (non-serializing DbContext), just load without locking
        if (semaphore == null)
        {
            LoadCore(entity, navigationName);
            return;
        }

        // Safe to Wait() synchronously because all async operations use ConfigureAwait(false)
        semaphore.Wait();
        try
        {
            // Double-check after acquiring lock
            if (IsLoaded(entity, navigationName))
                return;

            LoadCore(entity, navigationName);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void LoadCore(object entity, string navigationName)
    {
        if (!Context!.ChangeTracker.LazyLoadingEnabled)
            return;

        var entry = Context.Entry(entity);
        var navigation = entry.Navigation(navigationName);

        if (!navigation.IsLoaded)
        {
            _logger.NavigationLazyLoading(Context, entity, navigationName);
            navigation.Load();
        }

        SetLoaded(entity, navigationName, true);
    }

    /// <summary>
    /// Async lazy load - wraps the operation with semaphore.
    /// This can be called explicitly for async lazy loading scenarios.
    /// </summary>
    public async Task LoadAsync(
        object entity, 
        CancellationToken cancellationToken = default,
        [CallerMemberName] string navigationName = "")
    {
        if (_disposed || _detached || Context == null)
            return;

        if (IsLoaded(entity, navigationName))
            return;

        var semaphore = GetSemaphore();
        
        // If no semaphore (non-serializing DbContext), just load without locking
        if (semaphore == null)
        {
            await LoadCoreAsync(entity, navigationName, cancellationToken).ConfigureAwait(false);
            return;
        }

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (IsLoaded(entity, navigationName))
                return;

            await LoadCoreAsync(entity, navigationName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task LoadCoreAsync(object entity, string navigationName, CancellationToken cancellationToken)
    {
        if (!Context!.ChangeTracker.LazyLoadingEnabled)
            return;

        var entry = Context.Entry(entity);
        var navigation = entry.Navigation(navigationName);

        if (!navigation.IsLoaded)
        {
            _logger.NavigationLazyLoading(Context, entity, navigationName);
            await navigation.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        SetLoaded(entity, navigationName, true);
    }

    public bool Detaching(DbContext context, object entity)
    {
        _detached = true;
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        // Don't dispose the semaphore - it's owned by the DbContext
    }
}
