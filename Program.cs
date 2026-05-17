using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace QueryHeal.PoC;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 1. Add SignalR
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<QueryHealEventPublisher>();

        // 2. Register Interceptor as Singleton so it can hold state and publisher
        builder.Services.AddSingleton<QueryHealInterceptor>();

        // 3. Configure DbContext
        builder.Services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var interceptor = sp.GetRequiredService<QueryHealInterceptor>();
            options.UseSqlite("Data Source=queryheal.db");
            options.UseLazyLoadingProxies();
            options.AddInterceptors(interceptor);
        }, ServiceLifetime.Transient);

        // 4. Register a background service to trigger the N+1 simulation continuously or once
        builder.Services.AddHostedService<NPlusOneSimulatorService>();

        var app = builder.Build();

        // 5. Serve static files (index.html)
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // 6. Map SignalR Hub
        app.MapHub<QueryHealHub>("/queryhealhub");

        app.Run();
    }
}

// Background service to simulate the N+1 queries being triggered after the web server starts
public class NPlusOneSimulatorService : BackgroundService
{
    private readonly IServiceProvider _sp;

    public NPlusOneSimulatorService(IServiceProvider sp)
    {
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the app to start up and UI to potentially connect
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _sp.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Recreate DB cleanly for each run
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();

            SeedDatabase(context);

            Console.WriteLine("Database seeded. Running N+1 Scenario...\n");

            // Clear the change tracker so that EF Core doesn't use the cached entities from SeedDatabase.
            // This forces the N+1 lazy loading to actually hit the database.
            context.ChangeTracker.Clear();

            // 1. Query Orders (this is the "1" in N+1)
            var orders = context.Orders.ToList();

            Console.WriteLine($"Found {orders.Count} orders. Iterating and accessing Customer (Lazy Loading)...");

            // 2. Iterate and access navigation property, triggering "N" additional queries
            foreach (var order in orders)
            {
                var customerName = order.Customer.Name;
                if (customerName == null) throw new Exception("Customer missing");
            }

            Console.WriteLine("Finished N+1 Scenario. Interceptor should have logged warnings and broadcast to UI.");

            // Wait 10 seconds before simulating again
            await Task.Delay(10000, stoppingToken);
        }
    }

    private void SeedDatabase(AppDbContext context)
    {
        // Add 10 unique customers and 10 orders to trigger the N+1 threshold (> 5) on SELECT
        for (int i = 0; i < 10; i++)
        {
            var customer = new Customer { Name = $"Customer {i}" };
            context.Customers.Add(customer);

            context.Orders.Add(new Order
            {
                TotalAmount = 100 + (i * 10),
                Customer = customer
            });
        }

        context.SaveChanges();
    }
}
