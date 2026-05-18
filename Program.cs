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
        await Task.Delay(2000, stoppingToken);

        // 1. CHỈ TẠO VÀ SEED DATABASE 1 LẦN DUY NHẤT Ở ĐÂY
        using (var initScope = _sp.CreateScope())
        {
            var initContext = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
            initContext.Database.EnsureDeleted();
            initContext.Database.EnsureCreated();
            SeedDatabase(initContext);
            Console.WriteLine("Database initialized and seeded.\n");
        }

        // 2. VÒNG LẶP CHỈ LÀM NHIỆM VỤ QUERY
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _sp.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Console.WriteLine("\n[Simulator] Running N+1 Scenario...");

            // Xóa cache để ép EF Core phải chọc xuống DB thay vì lấy từ Memory
            context.ChangeTracker.Clear();

            var orders = context.Orders.ToList();

            // Vòng lặp gây ra N+1
            foreach (var order in orders)
            {
                var customerName = order.Customer?.Name;
            }

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
