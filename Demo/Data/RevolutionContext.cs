using Microsoft.EntityFrameworkCore;
using Demo.Models;

namespace Demo.Data
{
    public class RevolutionContext : DbContext
    {
        public RevolutionContext(DbContextOptions<RevolutionContext> options)
            : base(options)
        {
        }
        public DbSet<Revolution> Revolutions { get; set; } = null;
    }
}
