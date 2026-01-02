using BlazorDbConcurrencyRepro.Components;
using BlazorDbConcurrencyRepro.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// TRANSPARENT SERIALIZATION WITH LAZY LOADING SUPPORT
// 
// Architecture:
// 1. SerializingDbContext - Base class that owns a SemaphoreSlim and wraps DbSet/SaveChanges
// 2. ISerializingDbContext - Interface to expose the semaphore to lazy loader
// 3. SerializingLazyLoader - Gets the semaphore from DbContext via ISerializingDbContext
//
// The semaphore is owned by the DbContext, so both query operations AND lazy loading
// use the same lock, preventing "A second operation was started" errors.

// Register AppDbContext with lazy loading proxy support
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BlazorConcurrencyDemo;Trusted_Connection=True;MultipleActiveResultSets=true")
           .UseLazyLoadingProxies() // Enable Castle DynamicProxy for lazy loading
           .ReplaceService<ILazyLoader, SerializingLazyLoader>() // Use our serializing lazy loader
           .EnableSensitiveDataLogging()
           .EnableDetailedErrors();
});

var app = builder.Build();

// Ensure database is created and seeded
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    context.Database.EnsureDeleted();
    context.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
