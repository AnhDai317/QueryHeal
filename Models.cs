using System.Collections.Generic;

namespace QueryHeal.PoC;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Virtual is required for lazy loading proxies
    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
}

public class Order
{
    public int Id { get; set; }
    public decimal TotalAmount { get; set; }
    public int CustomerId { get; set; }

    // Virtual is required for lazy loading proxies
    public virtual Customer Customer { get; set; } = null!;
}
