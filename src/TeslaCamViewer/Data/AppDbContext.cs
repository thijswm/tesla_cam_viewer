using Microsoft.EntityFrameworkCore;

namespace TeslaCamViewer.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Clip> Clips => Set<Clip>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Camera> Cameras => Set<Camera>();
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
    public string Lat { get; set; } = string.Empty;
    public string Long { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public int Camera { get; set; }
    public DateTime TimeStamp { get; set; }
    public byte[]? Thumbnail { get; set; }
    public List<Clip> Clips { get; set; } = new();
    public List<Camera> Cameras { get; set; } = new();
}

public class Camera
{
    public int Id { get; set; }
    public string CameraName { get; set; } = string.Empty; // back, front, left_repeater, right_repeater
    public string MinioPath { get; set; } = string.Empty; // Path/key in MinIO bucket (e.g., "events/2026-02-04/front.mp4")
    public string BucketName { get; set; } = "teslacam-videos"; // MinIO bucket name
    public DateTime Timestamp { get; set; }
    public TimeSpan Duration { get; set; }
    public long FileSize { get; set; } // File size in bytes
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
}
