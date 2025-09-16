# One Attribute to Prevent Data Loss

**Concurrency Control to the Rescue**

A practical demonstration of EF Core's built-in concurrency control using the `[Timestamp]` attribute to prevent silent data loss in multi-user applications.

## The Problem

When multiple users work with the same data, the "last write wins" approach can silently lose important changes:

1. **User A** loads a product (Stock: 100)
2. **User B** purchases 30 units (Stock now: 70)  
3. **User A** submits their changes based on old data
4. **Result**: User B's purchase gets overwritten 💥

## The Solution

Add one attribute to prevent data loss:

```csharp
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Stock { get; set; }
    
    [Timestamp]
    public byte[] RowVersion { get; set; }  // ← This saves your data
}
```

## How It Works

1. **RowVersion** automatically updates on every change
2. EF Core includes it in UPDATE WHERE clauses
3. If data changed since loading, save fails with `DbUpdateConcurrencyException`
4. You handle the conflict appropriately

## Demo Endpoints

### Test Scenarios

| Endpoint | Description | Result |
|----------|-------------|--------|
| `POST /demo-concurrency-no-stock-change` | Raw SQL with no EF tracking | ✅ Survives concurrent changes |
| `POST /demo-concurrency-with-stock-change` | EF Core without RowVersion | ❌ Silent data loss |
| `POST /demo-with-rowversion` | EF Core with RowVersion protection | ✅ Exception prevents data loss |

### View Data

| Endpoint | Description |
|----------|-------------|
| `GET /products` | Products without concurrency protection |
| `GET /products-with-version` | Products with RowVersion |

## Running the Demo

```bash
# Clone the repository
git clone https://github.com/abdebek/db-concurrency-demo.git
cd db-concurrency-demo

# Run the application
dotnet run

# Test the endpoints
curl -X POST http://localhost:5000/demo-concurrency-with-stock-change
curl -X POST http://localhost:5000/demo-with-rowversion
```

## Handling Conflicts

When `DbUpdateConcurrencyException` occurs, you have three strategies:

### Store Wins (Reload)
```csharp
catch (DbUpdateConcurrencyException)
{
    await entry.ReloadAsync();
    // User sees current database values
}
```

### Client Wins (Force Update)
```csharp
catch (DbUpdateConcurrencyException ex)
{
    var entry = ex.Entries.Single();
    entry.OriginalValues.SetValues(entry.GetDatabaseValues());
    await context.SaveChangesAsync();
}
```

### Merge Strategy
```csharp
catch (DbUpdateConcurrencyException ex)
{
    var entry = ex.Entries.Single();
    var currentValues = entry.CurrentValues;
    var databaseValues = entry.GetDatabaseValues();
    
    // Custom logic to merge changes
    foreach (var property in entry.Metadata.GetProperties())
    {
        var current = currentValues[property];
        var database = databaseValues[property];
        
        // Implement your merge logic here
    }
    
    entry.OriginalValues.SetValues(databaseValues);
    await context.SaveChangesAsync();
}
```

## Best Practices

### ✅ Always Use RowVersion For:
- Multi-user applications
- Financial transactions
- Inventory management
- Any critical business data
- Long-running forms

### 📋 Implementation Checklist:
- [ ] Add `[Timestamp]` to entities
- [ ] Handle `DbUpdateConcurrencyException`
- [ ] Test concurrent scenarios
- [ ] Document conflict resolution strategy
- [ ] Create base entity with RowVersion

### 🏗️ Base Entity Pattern:
```csharp
public abstract class BaseEntity
{
    public int Id { get; set; }
    
    [Timestamp]
    public byte[] RowVersion { get; set; }
}

public class Product : BaseEntity
{
    public string Name { get; set; }
    public int Stock { get; set; }
}
```

## Performance Impact

- **Overhead**: ~1ms per operation
- **Storage**: 8 bytes per row
- **Network**: Minimal (RowVersion in queries)
- **Benefits**: Prevents costly data corruption

## Key Takeaways

| Aspect | Without RowVersion | With RowVersion |
|--------|-------------------|-----------------|
| **Data Loss** | ❌ Silent loss | ✅ Prevented |
| **Conflict Detection** | ❌ None | ✅ Exception thrown |
| **Implementation** | Nothing needed | One attribute |
| **Performance** | Baseline | Negligible overhead |
| **Data Integrity** | ❌ At risk | ✅ Protected |

## Resources

- [EF Core Concurrency Documentation](https://docs.microsoft.com/ef/core/saving/concurrency)
- [Handling Concurrency Conflicts](https://docs.microsoft.com/ef/core/saving/concurrency#handling-concurrency-conflicts)
- [Optimistic Concurrency Overview](https://docs.microsoft.com/ef/core/saving/concurrency#optimistic-concurrency)

---

**Remember**: The cost of data loss far exceeds the effort to implement concurrency control.

One attribute. Zero data loss. That's the power of `[Timestamp]`.