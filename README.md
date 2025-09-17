# Stop Silent Data Loss with One EF Core Attribute

**Concurrency Control to the Rescue**

A practical demonstration of EF Core's built-in concurrency control using the `[Timestamp]` attribute to prevent silent data loss in multi-user applications.

## The Problem

When multiple operations modify the **same field** concurrently, the last write wins and silently overwrites other changes:

1. **User A** loads a product (Stock: 100)
2. **User B** reduces stock to 75 via direct SQL
3. **User A** changes stock to 1000 and saves
4. **Result**: User B's stock change is silently overwritten 💥

*Note: Data loss only occurs when both operations modify the same field. If User A only changed the name/price (leaving stock untouched), User B's stock change would survive.*

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
| `POST /demo-concurrency-no-stock-change` | EF changes Name/Price only | ✅ Raw SQL Stock change survives |
| `POST /demo-concurrency-with-stock-change` | EF changes Stock too | ❌ Raw SQL Stock change overwritten |
| `POST /demo-with-rowversion` | EF Core with RowVersion protection | ✅ Exception prevents data loss |

### View Data

| Endpoint | Description |
|----------|-------------|
| `GET /products` | Products without concurrency protection |
| `GET /products-with-version` | Products with RowVersion |

## Running the Demo

```bash
# Clone the repository
git clone https://github.com/abdebek/efcore-db-concurrency-demo.git
cd efcore-db-concurrency-demo
dotnet restore

# Run the application
dotnet run

# Test the endpoints
curl -X POST https://localhost:7112/demo-concurrency-with-stock-change
curl -X POST https://localhost:7112/demo-with-rowversion
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

## Understanding the Scenarios

### Scenario 1: No Field Conflict (Safe)
```
1. User A loads product (Stock: 100)
2. Raw SQL: UPDATE Products SET Stock = 75
3. User A changes Name/Price only and saves
4. Result: Stock stays 75 ✅ (EF didn't touch Stock)
```

### Scenario 2: Same Field Conflict (Data Loss!)
```
1. User A loads product (Stock: 100) 
2. Raw SQL: UPDATE Products SET Stock = 75
3. User A changes Stock to 1000 and saves
4. Result: Stock becomes 1000 ❌ (Raw SQL change lost)
```

### Scenario 3: With RowVersion Protection
```
1. User A loads product (Stock: 100, RowVersion: ABC)
2. Raw SQL: UPDATE Products SET Stock = 75 (RowVersion: DEF)
3. User A tries to save changes
4. Result: DbUpdateConcurrencyException ✅ (Conflict detected)
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
