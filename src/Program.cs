using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ConcurrencyDemo;Trusted_Connection=true;"));

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    context.Database.EnsureCreated();

    // Seed data if empty
    if (!context.Products.Any())
    {
        context.Products.Add(new Product
        {
            Name = "Widget",
            Stock = 100,
            Price = 25.99m
        });

        context.SaveChanges();
    }

    if (!context.ProductsWithVersion.Any())
    {
        context.ProductsWithVersion.Add(new ProductWithVersion
        {
            Name = "Widget",
            Stock = 100,
            Price = 25.99m
        });

        context.SaveChanges();
    }
}

// Endpoint to demonstrate with concurrency issue
// Case 1: EF does NOT modify stock → raw SQL survives
app.MapPost("/demo-concurrency-no-stock-change", async (AppDbContext context) =>
{
    var result = new ConcurrencyDemoResult();

    // Step 1: Load product
    var product = await context.Products.FirstAsync();
    result.Steps.Add("1. Loaded product into EF Core context");
    result.InitialState = ProductStateDto.FromProduct(product);

    // Step 2: Modify only Name and Price
    product.Name = "Super Widget";
    product.Price = 29.99m;
    result.Steps.Add("2. Modified Name and Price in EF Core context (not saved yet)");
    result.AfterEFChanges = ProductStateDto.FromProduct(product);

    // Step 3: Raw SQL changes Stock
    await context.Database.ExecuteSqlRawAsync(
        "UPDATE Products SET Stock = 75 WHERE Id = {0}", product.Id);
    result.Steps.Add("3. Executed raw SQL to change Stock from 100 to 75");

    var dbState = await context.Database.SqlQueryRaw<ProductStateDto>(
        "SELECT Id, Name, Stock, Price FROM Products WHERE Id = {0}", product.Id)
        .FirstAsync();
    result.AfterRawSQLChange = dbState;

    // Step 4: Save EF changes (Stock untouched)
    await context.SaveChangesAsync();
    result.Steps.Add("4. Saved EF Core changes (Stock untouched)");
    result.AfterEFSave = ProductStateDto.FromProduct(product);

    // Step 5: Final state
    var finalState = await context.Database.SqlQueryRaw<ProductStateDto>(
        "SELECT Id, Name, Stock, Price FROM Products WHERE Id = {0}", product.Id)
        .FirstAsync();
    result.FinalState = finalState;

    return Results.Ok(new
    {
        Message = "Demo completed: EF did not modify stock, raw SQL survives",
        Details = result,
        Explanation = new
        {
            WhatHappened = "Raw SQL Stock=75 was preserved, since EF only updated Name and Price",
            SQLGenerated = "UPDATE Products SET Name='Super Widget', Price=29.99 WHERE Id=1",
            Lesson = "If EF doesn’t mark Stock as modified, SaveChanges() won’t overwrite raw SQL changes"
        }
    });
});


// Case 2: EF DOES modify stock → raw SQL gets overwritten
app.MapPost("/demo-concurrency-with-stock-change", async (AppDbContext context) =>
{
    var result = new ConcurrencyDemoResult();

    // Step 1: Load product
    var product = await context.Products.FirstAsync();
    result.Steps.Add("1. Loaded product into EF Core context");
    result.InitialState = ProductStateDto.FromProduct(product);

    // Step 2: Modify Name, Price, and Stock
    product.Name = "Super Widget";
    product.Price = 29.99m;
    product.Stock = 1000; // EF will mark Stock as modified
    result.Steps.Add("2. Modified Name, Stock, and Price in EF Core context (not saved yet)");
    result.AfterEFChanges = ProductStateDto.FromProduct(product);

    // Step 3: Raw SQL changes Stock again
    await context.Database.ExecuteSqlRawAsync(
        "UPDATE Products SET Stock = 75 WHERE Id = {0}", product.Id);
    result.Steps.Add("3. Executed raw SQL to change Stock to 75");

    var dbState = await context.Database.SqlQueryRaw<ProductStateDto>(
        "SELECT Id, Name, Stock, Price FROM Products WHERE Id = {0}", product.Id)
        .FirstAsync();
    result.AfterRawSQLChange = dbState;

    // Step 4: Save EF changes (Stock included!)
    await context.SaveChangesAsync();
    result.Steps.Add("4. Saved EF Core changes (Stock overwritten)");
    result.AfterEFSave = ProductStateDto.FromProduct(product);

    // Step 5: Final state
    var finalState = await context.Database.SqlQueryRaw<ProductStateDto>(
        "SELECT Id, Name, Stock, Price FROM Products WHERE Id = {0}", product.Id)
        .FirstAsync();
    result.FinalState = finalState;

    return Results.Ok(new
    {
        Message = "Demo completed: EF overwrote stock",
        Details = result,
        Explanation = new
        {
            WhatHappened = "Raw SQL Stock=75 was overwritten because EF also updated Stock",
            SQLGenerated = "UPDATE Products SET Name='Super Widget', Stock=1000, Price=29.99 WHERE Id=1",
            Lesson = "If EF marks Stock as modified, SaveChanges() overwrites raw SQL changes"
        }
    });
});


// Endpoint to demonstrate with concurrency token
app.MapPost("/demo-with-rowversion", async (AppDbContext context) =>
{
    var result = new ConcurrencyWithRowVersionResult();

    try
    {
        // Step 1: Load product with rowversion
        var productWithVersion = await context.ProductsWithVersion.FirstAsync();
        result.Steps.Add("1. Loaded product with RowVersion into EF Core context");
        result.InitialState = ProductWithVersionStateDto.FromEntity(productWithVersion);

        // Step 2: Modify tracked entity
        productWithVersion.Name = "Super Widget v2";
        productWithVersion.Price = 35.99m;
        result.Steps.Add("2. Modified Name and Price in EF Core context");
        result.AfterEFChanges = ProductWithVersionStateDto.FromEntity(productWithVersion);

        // Step 3: Execute raw SQL (changes Stock + RowVersion)
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE ProductsWithVersion SET Stock = 50 WHERE Id = {0}", productWithVersion.Id);
        result.Steps.Add("3. Executed raw SQL to change Stock - this updates RowVersion!");

        var dbState = await context.Database.SqlQueryRaw<ProductWithVersionDto>(
            "SELECT Id, Name, Stock, Price, RowVersion FROM ProductsWithVersion WHERE Id = {0}",
            productWithVersion.Id).FirstAsync();

        result.AfterRawSQLChange = ProductWithVersionStateDto.FromDto(dbState);

        // Step 4: Try saving EF changes (should throw)
        await context.SaveChangesAsync();
        result.Steps.Add("4. Attempted to save EF Core changes");
    }
    catch (DbUpdateConcurrencyException ex)
    {
        result.ExceptionThrown = true;
        result.ExceptionMessage = ex.Message;
        result.Steps.Add("4. DbUpdateConcurrencyException thrown - concurrency conflict detected!");
    }

    // Step 5: Final state
    var finalState = await context.Database.SqlQueryRaw<ProductWithVersionDto>(
        "SELECT Id, Name, Stock, Price, RowVersion FROM ProductsWithVersion WHERE Id = {0}", 1)
        .FirstAsync();
    result.FinalState = ProductWithVersionStateDto.FromDto(finalState);

    // Build response
    var response = new ConcurrencyWithRowVersionResponse
    {
        Message = result.ExceptionThrown
            ? "Concurrency conflict detected and prevented data loss"
            : "Unexpected: No concurrency exception thrown",
        Details = result,
        Explanation = new ExplanationDto
        {
            WhatHappened = result.ExceptionThrown
                ? "Raw SQL updated RowVersion, EF Core detected conflict and threw exception"
                : "Something unexpected occurred",
            Protection = "RowVersion prevents silent data overwrites",
            NextSteps = "Handle exception by reloading entity and reapplying changes"
        }
    };

    return Results.Ok(response);
});

// Reset endpoints for testing
app.MapPost("/reset-products", async (AppDbContext context) =>
{
    await context.Database.ExecuteSqlRawAsync("DELETE FROM Products");
    context.Products.Add(new Product { Name = "Widget", Stock = 100, Price = 25.99m });
    await context.SaveChangesAsync();
    return Results.Ok("Products table reset");
});

app.MapPost("/reset-products-with-version", async (AppDbContext context) =>
{
    await context.Database.ExecuteSqlRawAsync("DELETE FROM ProductsWithVersion");
    context.ProductsWithVersion.Add(new ProductWithVersion { Name = "Widget", Stock = 100, Price = 25.99m });
    await context.SaveChangesAsync();
    return Results.Ok("ProductsWithVersion table reset");
});

// View current state endpoints
app.MapGet("/products", async (AppDbContext context) =>
    await context.Products.ToListAsync());

app.MapGet("/products-with-version", async (AppDbContext context) =>
    await context.ProductsWithVersion.Select(p => new
    {
        p.Id,
        p.Name,
        p.Stock,
        p.Price,
        RowVersion = Convert.ToBase64String(p.RowVersion)
    }).ToListAsync());

app.Run();



// Entity without concurrency control
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Stock { get; set; }
    public decimal Price { get; set; }
}

// Entity with concurrency control
public class ProductWithVersion
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Stock { get; set; }
    public decimal Price { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = new byte[8];
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products { get; set; }
    public DbSet<ProductWithVersion> ProductsWithVersion { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure Product entity
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
        });

        // Configure ProductWithVersion entity with concurrency token
        modelBuilder.Entity<ProductWithVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
            entity.Property(e => e.RowVersion)
                .IsRowVersion() // This configures it as a concurrency token
                .ValueGeneratedOnAddOrUpdate();
        });
    }
}


#region DTOs
// DTOs for raw SQL queries
public class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Stock { get; set; }
    public decimal Price { get; set; }
}

public class ProductWithVersionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Stock { get; set; }
    public decimal Price { get; set; }
    public byte[] RowVersion { get; set; } = new byte[8];
}


// DTO to represent product state snapshots
public class ProductStateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Stock { get; set; }
    public decimal Price { get; set; }

    public static ProductStateDto FromProduct(Product product) =>
        new ProductStateDto
        {
            Id = product.Id,
            Name = product.Name,
            Stock = product.Stock,
            Price = product.Price
        };
}

// Container for demo details
public class ConcurrencyDemoResult
{
    public List<string> Steps { get; set; } = new();
    public ProductStateDto? InitialState { get; set; }
    public ProductStateDto? AfterEFChanges { get; set; }
    public ProductStateDto? AfterRawSQLChange { get; set; }
    public ProductStateDto? AfterEFSave { get; set; }
    public ProductStateDto? FinalState { get; set; }
}

// DTO for snapshot of ProductWithVersion (RowVersion as Base64)
public class ProductWithVersionStateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Stock { get; set; }
    public decimal Price { get; set; }
    public string RowVersion { get; set; } = "";

    public static ProductWithVersionStateDto FromEntity(ProductWithVersion entity) =>
        new ProductWithVersionStateDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Stock = entity.Stock,
            Price = entity.Price,
            RowVersion = Convert.ToBase64String(entity.RowVersion)
        };

    public static ProductWithVersionStateDto FromDto(ProductWithVersionDto dto) =>
        new ProductWithVersionStateDto
        {
            Id = dto.Id,
            Name = dto.Name,
            Stock = dto.Stock,
            Price = dto.Price,
            RowVersion = Convert.ToBase64String(dto.RowVersion)
        };
}

// Container for demo details
public class ConcurrencyWithRowVersionResult
{
    public List<string> Steps { get; set; } = new();
    public ProductWithVersionStateDto? InitialState { get; set; }
    public ProductWithVersionStateDto? AfterEFChanges { get; set; }
    public ProductWithVersionStateDto? AfterRawSQLChange { get; set; }
    public bool ExceptionThrown { get; set; }
    public string ExceptionMessage { get; set; } = "";
    public ProductWithVersionStateDto? FinalState { get; set; }
}

// Final response container
public class ConcurrencyWithRowVersionResponse
{
    public string Message { get; set; } = "";
    public ConcurrencyWithRowVersionResult Details { get; set; } = new();
    public ExplanationDto Explanation { get; set; } = new();
}

public class ExplanationDto
{
    public string WhatHappened { get; set; } = "";
    public string Protection { get; set; } = "";
    public string NextSteps { get; set; } = "";
}

#endregion
