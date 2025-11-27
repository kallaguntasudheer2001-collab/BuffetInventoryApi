using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

public class Program
{
    public static void Main(string[] args)
    {
        QuestPDF.Settings.License = LicenseType.Community; // required by QuestPDF

        var builder = WebApplication.CreateBuilder(args);

        // Configure EF Core with SQLite
        builder.Services.AddDbContext<InventoryContext>(options =>
            options.UseSqlite("Data Source=inventory.db"));

        // Swagger/OpenAPI
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Buffet Inventory API",
                Version = "v1"
            });
        });

        var app = builder.Build();

        // Ensure database is created (no migrations needed) + SEED DATA
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryContext>();
            db.Database.EnsureCreated();
            SeedData.Seed(db);   // seeds items + customers (+ optional expenses)
        }

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Serve static files (wwwroot)
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseHttpsRedirection();

        #region Items Endpoints

        // Get all items
        app.MapGet("/api/items", async (InventoryContext db) =>
            await db.Items.ToListAsync());

        // Create or add item
        app.MapPost("/api/items", async (Item item, InventoryContext db) =>
        {
            db.Items.Add(item);
            await db.SaveChangesAsync();
            return Results.Created($"/api/items/{item.Id}", item);
        });

        // Update item
        app.MapPut("/api/items/{id:int}", async (int id, Item updated, InventoryContext db) =>
        {
            var existing = await db.Items.FindAsync(id);
            if (existing is null) return Results.NotFound();

            existing.Name = updated.Name;
            existing.Category = updated.Category;
            existing.Unit = updated.Unit;
            existing.ReorderLevel = updated.ReorderLevel;

            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });

        // Delete item
        app.MapDelete("/api/items/{id:int}", async (int id, InventoryContext db) =>
        {
            var item = await db.Items.FindAsync(id);
            if (item is null) return Results.NotFound();
            db.Items.Remove(item);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        #endregion

        #region Customers Endpoints

        app.MapGet("/api/customers", async (InventoryContext db) =>
            await db.Customers.ToListAsync());

        app.MapPost("/api/customers", async (Customer customer, InventoryContext db) =>
        {
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            return Results.Created($"/api/customers/{customer.Id}", customer);
        });

        app.MapDelete("/api/customers/{id:int}", async (int id, InventoryContext db) =>
        {
            var c = await db.Customers.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.Customers.Remove(c);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        #endregion

        #region Transactions Endpoints (for stock IN / OUT)

        app.MapGet("/api/transactions", async (int? itemId, InventoryContext db) =>
        {
            var query = db.Transactions.AsQueryable();
            if (itemId.HasValue)
                query = query.Where(t => t.ItemId == itemId.Value);

            return await query
                .OrderByDescending(t => t.Date)
                .ToListAsync();
        });

        app.MapPost("/api/transactions", async (Transaction tx, InventoryContext db) =>
        {
            if (tx.Quantity <= 0 || (tx.Type != "IN" && tx.Type != "OUT"))
                return Results.BadRequest("Type must be 'IN' or 'OUT' and Quantity > 0.");

            var item = await db.Items.FindAsync(tx.ItemId);
            if (item is null) return Results.BadRequest("Invalid ItemId.");

            tx.Date = tx.Date == default ? DateTime.UtcNow : tx.Date;

            db.Transactions.Add(tx);
            await db.SaveChangesAsync();

            var response = new
            {
                tx.Id,
                tx.ItemId,
                tx.Date,
                tx.Type,
                tx.Quantity,
                tx.Reference,
                tx.Notes
            };

            return Results.Created($"/api/transactions/{tx.Id}", response);
        });

        #endregion

        #region Orders Endpoints

        // Get all orders with related customer + item
        app.MapGet("/api/orders", async (InventoryContext db) =>
            await db.Orders
                .Include(o => o.Customer)
                .Include(o => o.Item)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync());

        // Create order (and auto create OUT transaction)
        app.MapPost("/api/orders", async (Order order, InventoryContext db) =>
        {
            var item = await db.Items.FindAsync(order.ItemId);
            var customer = await db.Customers.FindAsync(order.CustomerId);
            if (item == null || customer == null)
                return Results.BadRequest("Invalid ItemId or CustomerId.");

            order.OrderDate = DateTime.UtcNow;

            // Deduct stock via OUT transaction
            var tx = new Transaction
            {
                ItemId = item.Id,
                Type = "OUT",
                Quantity = order.Quantity,
                Date = DateTime.UtcNow,
                Reference = $"Order #{order.Id}"
            };

            db.Orders.Add(order);
            db.Transactions.Add(tx);
            await db.SaveChangesAsync();

            return Results.Created($"/api/orders/{order.Id}", order);
        });

        // Generate Invoice PDF
        app.MapGet("/api/orders/{id:int}/invoice", async (int id, InventoryContext db) =>
        {
            var order = await db.Orders
                .Include(o => o.Customer)
                .Include(o => o.Item)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order is null) return Results.NotFound();

            var pdf = GenerateInvoicePdf(order);
            return Results.File(pdf, "application/pdf", $"Invoice_Order_{id}.pdf");
        });

        #endregion

        #region Stock Endpoints

        // Get stock summary for all items
        app.MapGet("/api/stock", async (InventoryContext db) =>
        {
            var stock = await db.Items
                .Include(i => i.Transactions)
                .Select(item => new StockDto
                {
                    ItemId = item.Id,
                    ItemName = item.Name,
                    Unit = item.Unit,
                    ReorderLevel = item.ReorderLevel,
                    TotalIn = item.Transactions
                        .Where(t => t.Type == "IN")
                        .Sum(t => (int?)t.Quantity) ?? 0,
                    TotalOut = item.Transactions
                        .Where(t => t.Type == "OUT")
                        .Sum(t => (int?)t.Quantity) ?? 0
                })
                .ToListAsync();

            foreach (var s in stock)
            {
                s.CurrentStock = s.TotalIn - s.TotalOut;
                s.BelowReorder = s.CurrentStock < s.ReorderLevel;
            }

            return stock;
        });

        #endregion

        #region Expenses Endpoints

        // Get last 100 expenses (for expenses tab)
        app.MapGet("/api/expenses", async (InventoryContext db) =>
            await db.Expenses
                .OrderByDescending(e => e.Date)
                .Take(100)
                .ToListAsync());

        // Add expense (salary, transport, electricity, etc.)
        app.MapPost("/api/expenses", async (Expense exp, InventoryContext db) =>
        {
            if (exp.Amount <= 0)
                return Results.BadRequest("Amount must be > 0.");

            if (exp.Date == default)
                exp.Date = DateTime.UtcNow;

            db.Expenses.Add(exp);
            await db.SaveChangesAsync();
            return Results.Created($"/api/expenses/{exp.Id}", exp);
        });

        #endregion

        #region Daily Summary Endpoint

        // Daily orders + profit + salary breakdown
        // Example: GET /api/summary/daily?date=2025-11-27
        app.MapGet("/api/summary/daily", async (DateTime? date, InventoryContext db) =>
        {
            var day = (date ?? DateTime.UtcNow.Date).Date;
            var nextDay = day.AddDays(1);

            // Orders for that day
            var orders = await db.Orders
                .Where(o => o.OrderDate >= day && o.OrderDate < nextDay)
                .ToListAsync();

            var totalOrders = orders.Count;
            var totalOrderValue = orders.Sum(o => o.Quantity * o.UnitPrice);

            // Expenses for that day
            var expenses = await db.Expenses
                .Where(e => e.Date >= day && e.Date < nextDay)
                .ToListAsync();

            var totalExpenses = expenses.Sum(e => e.Amount);
            var salaryExpenses = expenses
                .Where(e => e.Category == "Salary")
                .Sum(e => e.Amount);
            var otherExpenses = totalExpenses - salaryExpenses;

            var profit = totalOrderValue - totalExpenses;

            var summary = new
            {
                date = day.ToString("yyyy-MM-dd"),
                totalOrders,
                totalOrderValue,
                totalExpenses,
                salaryExpenses,
                otherExpenses,
                profit
            };

            return Results.Ok(summary);
        });

        #endregion

        app.Run();
    }

    // PDF generator inside Program class
    private static byte[] GenerateInvoicePdf(Order order)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(30);
                page.Header().Text("Sudheer Eco Plates - Invoice").FontSize(20).Bold();

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Item().Text($"Invoice ID: {order.Id}");
                    col.Item().Text($"Date: {order.OrderDate:yyyy-MM-dd HH:mm}");
                    col.Item().Text($"Customer: {order.Customer?.Name}");
                    col.Item().Text($"Phone: {order.Customer?.Phone}");
                    col.Item().Text($"Address: {order.Customer?.Address}");
                    col.Item().LineHorizontal(1);

                    col.Item().Text($"Item: {order.Item?.Name}");
                    col.Item().Text($"Quantity: {order.Quantity}");
                    col.Item().Text($"Unit Price: ₹{order.UnitPrice}");
                    col.Item().Text($"Total: ₹{order.Quantity * order.UnitPrice}").Bold();
                });

                page.Footer().AlignCenter().Text("Thank you for your business! – Sudheer Eco Plates");
            });
        });

        return doc.GeneratePdf();
    }
}

#region Models & DbContext

public class Item
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";   // FinishedProduct, PurchasedMaterial, etc.
    public string Unit { get; set; } = "Pieces"; // Pieces, Rolls, etc.
    public int ReorderLevel { get; set; } = 0;

    public List<Transaction> Transactions { get; set; } = new();
}

public class Transaction
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }

    public DateTime Date { get; set; }
    public string Type { get; set; } = "IN"; // IN or OUT
    public int Quantity { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
}

public class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int ItemId { get; set; }
    public Item? Item { get; set; }

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; } = 0;
    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
}

public class StockDto
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public string Unit { get; set; } = "";
    public int ReorderLevel { get; set; }
    public int TotalIn { get; set; }
    public int TotalOut { get; set; }
    public int CurrentStock { get; set; }
    public bool BelowReorder { get; set; }
}

public class Expense
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string Category { get; set; } = "";   // Salary, Materials, Electricity, Transport, Rent, Other
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}

public class InventoryContext : DbContext
{
    public InventoryContext(DbContextOptions<InventoryContext> options) : base(options) { }

    public DbSet<Item> Items => Set<Item>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Expense> Expenses => Set<Expense>();
}

#endregion

#region Seed Data

public static class SeedData
{
    public static void Seed(InventoryContext context)
    {
        // Seed ONLY core items: plates, glasses, table rolls
        if (!context.Items.Any())
        {
            var items = new[]
            {
                // PLATES
                new Item
                {
                    Name = "Buffet Plate 10\" Eco",
                    Category = "FinishedProduct",
                    Unit = "Pieces",
                    ReorderLevel = 2000
                },
                new Item
                {
                    Name = "Buffet Plate 12\" Eco",
                    Category = "FinishedProduct",
                    Unit = "Pieces",
                    ReorderLevel = 2000
                },

                // GLASSES
                new Item
                {
                    Name = "Water Glass 250 ml",
                    Category = "FinishedProduct",
                    Unit = "Pieces",
                    ReorderLevel = 1500
                },
                new Item
                {
                    Name = "Juice Glass 300 ml",
                    Category = "FinishedProduct",
                    Unit = "Pieces",
                    ReorderLevel = 1500
                },

                // TABLE ROLLS
                new Item
                {
                    Name = "Table Roll – White",
                    Category = "PurchasedMaterial",
                    Unit = "Rolls",
                    ReorderLevel = 50
                },
                new Item
                {
                    Name = "Table Roll – Printed",
                    Category = "PurchasedMaterial",
                    Unit = "Rolls",
                    ReorderLevel = 50
                }
            };

            context.Items.AddRange(items);
        }

        // Seed Customers
        if (!context.Customers.Any())
        {
            var customers = new[]
            {
                new Customer { Name = "Sai Catering Services", Phone = "9876543210", Address = "Tanguturu, Andhra Pradesh" },
                new Customer { Name = "Sri Lakshmi Tiffins", Phone = "9876501234", Address = "Ongole, Andhra Pradesh" },
                new Customer { Name = "Haritha Function Hall", Phone = "9900112233", Address = "Nellore, Andhra Pradesh" }
            };

            context.Customers.AddRange(customers);
        }

        // Optional: Seed a couple of example expenses
        if (!context.Expenses.Any())
        {
            var today = DateTime.UtcNow.Date;
            var expenses = new[]
            {
                new Expense
                {
                    Date = today,
                    Category = "Salary",
                    Amount = 1500,
                    Description = "Daily wages for workers"
                },
                new Expense
                {
                    Date = today,
                    Category = "Electricity",
                    Amount = 500,
                    Description = "Power for machines"
                }
            };

            context.Expenses.AddRange(expenses);
        }

        context.SaveChanges();
    }
}

#endregion
