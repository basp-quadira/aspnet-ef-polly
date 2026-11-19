using Microsoft.EntityFrameworkCore;

namespace Host;

public class FooContext(DbContextOptions<FooContext> options) 
    : DbContext(options)
{
    public DbSet<Item> Items { get; set; }
}