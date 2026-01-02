# Blazor DbContext Concurrency Repro & Solution

This project demonstrates and solves the concurrent DbContext access issue in Blazor Server applications (related to [dotnet/aspnetcore#54616](https://github.com/dotnet/aspnetcore/issues/54616)).

## The Problem

In Blazor Server apps:
- While execution is single-threaded (runs inside a synchronization context), multiple components doing async work can **interleave their renderings**
- This can trigger DB calls before previous calls have finished
- This causes the error: `"A second operation was started on this context instance before a previous operation completed"`

Even when using `IDbContextFactory<T>` as recommended, the EF Core DbContext is not thread-safe and concurrent operations cause errors.

## The Solution: SerializingDbContext

This solution provides a comprehensive approach that serializes all DbContext operations, **including lazy loading**. It consists of several cooperating classes:

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    SerializingDbContext                      │
│  - Owns the SemaphoreSlim(1,1)                              │
│  - Overrides Set<T>() to return SerializingDbSet<T>         │
│  - Wraps SaveChanges with semaphore acquisition             │
└─────────────────────────────────────────────────────────────┘
           │                                    │
           ▼                                    ▼
┌─────────────────────┐            ┌─────────────────────────┐
│  SerializingDbSet   │            │  SerializingLazyLoader  │
│  - Wraps all LINQ   │            │  - Wraps ILazyLoader    │
│    query operations │            │  - Acquires semaphore   │
│  - Uses semaphore   │            │    for lazy loading     │
└─────────────────────┘            └─────────────────────────┘
           │                                    │
           └──────────────┬─────────────────────┘
                          ▼
                 ┌─────────────────┐
                 │  SemaphoreSlim  │
                 │      (1,1)      │
                 └─────────────────┘
```

### Key Files

| File | Purpose |
|------|---------|
| `Data/DbContextOperationLock.cs` | `ISerializingDbContext` interface exposing the semaphore |
| `Data/SerializingDbContext.cs` | Base DbContext class with automatic serialization |
| `Data/SerializingDbSet.cs` | DbSet wrapper that serializes all query operations |
| `Data/SerializingLazyLoader.cs` | Custom `ILazyLoader` for lazy loading support |

---

## Tutorial: How to Use SerializingDbContext

### Step 1: Add the Infrastructure Classes

Copy these files into your project's `Data/` folder:
- `DbContextOperationLock.cs`  
- `SerializingDbContext.cs`
- `SerializingDbSet.cs`
- `SerializingLazyLoader.cs`

### Step 2: Inherit from SerializingDbContext

Change your DbContext to inherit from `SerializingDbContext` instead of `DbContext`:

```csharp
// Before:
public class AppDbContext : DbContext
{
    public DbSet<Todo> Todos { get; set; }
    public DbSet<Category> Categories { get; set; }
}

// After:
public class AppDbContext : SerializingDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    
    public DbSet<Todo> Todos { get; set; }
    public DbSet<Category> Categories { get; set; }
}
```

### Step 3: Configure Services in Program.cs

```csharp
// Enable MARS in your connection string (required for lazy loading)
var connectionString = "Server=(localdb)\\mssqllocaldb;Database=MyApp;Trusted_Connection=True;MultipleActiveResultSets=true";

// Configure DbContext with lazy loading proxies and custom lazy loader
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(connectionString)
        .UseLazyLoadingProxies()
        .ReplaceService<ILazyLoader, SerializingLazyLoader>();
});
```

### Step 4: Use in Components

Use DbContext normally in your Blazor components. The serialization is transparent:

```razor
@page "/todos"
@inject AppDbContext DbContext

<h3>Todos</h3>
@if (todos == null)
{
    <p>Loading...</p>
}
else
{
    @foreach (var todo in todos)
    {
        <div>
            @todo.Title - @todo.Category?.Name  <!-- Lazy loading works! -->
        </div>
    }
}

@code {
    private List<Todo>? todos;

    protected override async Task OnInitializedAsync()
    {
        // This is automatically serialized with other concurrent operations
        todos = await DbContext.Set<Todo>().ToListAsync();
    }
}
```

---

## How It Works

### The Key Insight: ConfigureAwait(false)

The critical piece that makes this solution work—especially for lazy loading—is using `ConfigureAwait(false)` on all async operations.

In Blazor Server, code runs on a synchronization context. When lazy loading triggers (which is synchronous), it needs to wait for the semaphore. Without `ConfigureAwait(false)`, this would deadlock:

```
1. Component A holds the semaphore, awaiting a query
2. Component B tries to lazy load, waits synchronously for semaphore
3. Component A's query completes, but the continuation needs the sync context
4. Component B is blocking the sync context, waiting for the semaphore
5. DEADLOCK!
```

With `ConfigureAwait(false)`, Component A's continuation runs on a thread pool thread instead:

```
1. Component A holds the semaphore, awaiting a query (with ConfigureAwait(false))
2. Component B tries to lazy load, waits synchronously for semaphore
3. Component A's query completes, continuation runs on thread pool (not sync context)
4. Component A releases semaphore on thread pool thread
5. Component B acquires semaphore, lazy loading proceeds
6. SUCCESS!
```

### SerializingDbSet

The `SerializingDbSet<T>` wraps all LINQ operations and ensures the semaphore is held for the entire enumeration:

```csharp
// When you call:
await dbContext.Set<Todo>().Where(t => t.IsComplete).ToListAsync();

// SerializingDbSet ensures:
// 1. Semaphore is acquired BEFORE the query starts
// 2. Semaphore is held during entire result enumeration
// 3. Semaphore is released AFTER enumeration completes (or on error)
```

### SerializingLazyLoader

The `SerializingLazyLoader` replaces EF Core's default lazy loader and acquires the semaphore synchronously:

```csharp
public void Load(object entity, string navigationName)
{
    // Acquire semaphore synchronously - safe because all async ops use ConfigureAwait(false)
    _semaphore.Wait();
    try
    {
        // Load the navigation property
    }
    finally
    {
        _semaphore.Release();
    }
}
```

---

## Project Structure

```
├── Data/
│   ├── AppDbContext.cs              # Your DbContext (inherits SerializingDbContext)
│   ├── DbContextOperationLock.cs    # ISerializingDbContext interface
│   ├── SerializingDbContext.cs      # Base class with serialization
│   ├── SerializingDbSet.cs          # Query wrapper
│   ├── SerializingLazyLoader.cs     # Lazy loading support
│   ├── Todo.cs                      # Entity
│   └── Category.cs                  # Entity
├── Components/
│   ├── TodoList.razor               # Component loading todos
│   ├── CategoryList.razor           # Component loading categories  
│   └── Pages/
│       └── Home.razor               # Main page with multiple components
└── Program.cs                       # Service configuration
```

## Running the Demo

```bash
dotnet run
```

Navigate to the URL shown in the console.

The page shows:
- 2 TodoList components and 2 CategoryList components
- Each component loads data and accesses navigation properties (lazy loading)
- Click "Refresh All Components Simultaneously" to trigger concurrent loads
- All operations complete successfully, serialized automatically

---

## Trade-offs

**Pros:**
- ✅ Eliminates concurrent access errors completely
- ✅ **Supports lazy loading** (most solutions don't!)
- ✅ Works transparently with existing LINQ code
- ✅ No changes needed to component code
- ✅ Handles all operation types (queries, saves, lazy loads)

**Cons:**
- ⚠️ Serializes all DB operations (no true parallelism within one DbContext)
- ⚠️ Slight latency increase when multiple operations queue up
- ⚠️ Requires inheriting from `SerializingDbContext`
- ⚠️ Uses `ConfigureAwait(false)` so code after await runs on thread pool

---

## Alternative Approaches

1. **Create DbContext per operation**: Use `IDbContextFactory<T>` and create a new context for each query. Doesn't support lazy loading across operations.

2. **Disable lazy loading**: Use eager loading (`.Include()`) for all navigation properties. Requires knowing upfront what data you need.

3. **DbCommandInterceptor**: Serialize at the command level. Simpler but doesn't handle all EF Core scenarios properly.

4. **Explicit synchronization**: Use `SemaphoreSlim` manually in each component. Error-prone and repetitive.

---

## Requirements

- .NET 8.0 or later
- EF Core 8.0 or later
- MARS enabled in connection string for SQL Server
- `Microsoft.EntityFrameworkCore.Proxies` package for lazy loading

## References

- [Blazor and EF Core Guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core)
- [EF Core Lazy Loading](https://learn.microsoft.com/en-us/ef/core/querying/related-data/lazy)
- [GitHub Issue #54616](https://github.com/dotnet/aspnetcore/issues/54616)
