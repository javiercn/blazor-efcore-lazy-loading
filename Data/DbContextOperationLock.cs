namespace BlazorDbConcurrencyRepro.Data;

/// <summary>
/// Interface for DbContexts that support operation serialization.
/// SerializingLazyLoader uses this to access the shared semaphore.
/// </summary>
public interface ISerializingDbContext
{
    /// <summary>
    /// Gets the semaphore used to serialize operations on this DbContext.
    /// </summary>
    SemaphoreSlim OperationSemaphore { get; }
}
