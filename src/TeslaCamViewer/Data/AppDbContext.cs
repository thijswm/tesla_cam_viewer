using Microsoft.EntityFrameworkCore;

namespace TeslaCamViewer.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Clip> Clips => Set<Clip>();
    public DbSet<Event> Events => Set<Event>();
}

public class Clip
{
    public int Id { get; set; }
    public string Camera { get; set; } = string.Empty; // back, front, left_repeater, right_repeater
    public string Path { get; set; } = string.Empty;   // absolute path inside container mount
    public DateTime Timestamp { get; set; }
    public int? EventId { get; set; }
    public Event? Event { get; set; }
}

public class Event
{
    public int Id { get; set; }
    public string FolderName { get; set; } = string.Empty; // e.g. 2026-02-04_10-25-59
    public string Type { get; set; } = "unknown";         // from event.json if present
    public DateTime CreatedAt { get; set; }
    public string Source { get; set; } = "Sentry"; // or Saved
    public List<Clip> Clips { get; set; } = new();
}
