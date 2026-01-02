using Microsoft.EntityFrameworkCore;

namespace BlazorDbConcurrencyRepro.Data;

/// <summary>
/// Application DbContext that automatically serializes all concurrent operations.
/// By inheriting from SerializingDbContext, all DbSet access and SaveChanges operations
/// are automatically serialized - no manual locking code needed!
/// 
/// SerializingLazyLoader accesses the semaphore via ISerializingDbContext interface,
/// so lazy loading operations are also serialized.
/// </summary>
public class AppDbContext : SerializingDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) 
        : base(options)
    {
    }

    // Simple DbSet properties - the base class Set<T>() automatically wraps them
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Seed some initial data
        modelBuilder.Entity<Category>().HasData(
            new Category { Id = 1, Name = "Work" },
            new Category { Id = 2, Name = "Personal" },
            new Category { Id = 3, Name = "Shopping" }
        );

        modelBuilder.Entity<TodoItem>().HasData(
            new TodoItem { Id = 1, Title = "Review PR", CategoryId = 1, IsCompleted = false },
            new TodoItem { Id = 2, Title = "Buy groceries", CategoryId = 3, IsCompleted = false },
            new TodoItem { Id = 3, Title = "Call mom", CategoryId = 2, IsCompleted = true },
            new TodoItem { Id = 4, Title = "Write report", CategoryId = 1, IsCompleted = false },
            new TodoItem { Id = 5, Title = "Exercise", CategoryId = 2, IsCompleted = false }
        );
    }
}


public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int CategoryId { get; set; }
    
    // Virtual for lazy loading proxy
    public virtual Category? Category { get; set; }
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    
    // Virtual for lazy loading proxy
    public virtual ICollection<TodoItem> TodoItems { get; set; } = new List<TodoItem>();
}
