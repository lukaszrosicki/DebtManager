using DebtManager.Models;
using Microsoft.EntityFrameworkCore;

namespace DebtManager.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) 
        : base(options)
    {
    }

    public DbSet<Dlug> Dlugi { get; set; }
    public DbSet<Rata> Raty { get; set; }
    public DbSet<Nadplata> Nadplaty { get; set; }
    public DbSet<ZmianaOprocentowania> ZmianyOprocentowania { get; set; }
}
