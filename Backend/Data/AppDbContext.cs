using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Data;

public class AppDbContext : DbContext
{
    public DbSet<TelemetryRecord> Telemetries => Set<TelemetryRecord>();
    public DbSet<AiDecisionRecord> AiDecisions => Set<AiDecisionRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite("Data Source=greenhouse.db");
        }
    }
}
