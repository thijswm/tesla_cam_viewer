using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;
using TeslaCamViewer.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddMudServices();

builder.Services.AddHostedService<ClipScanner>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database?.Migrate();
}

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.MapGet("/api/health", () => Results.Ok("ok"));
app.MapGet("/api/events", async (AppDbContext db) =>
    await db.Events.OrderByDescending(e => e.CreatedAt).ToListAsync());
app.MapGet("/api/clips", async (AppDbContext db) =>
    await db.Clips.OrderByDescending(c => c.Timestamp).Take(500).ToListAsync());

app.Run();
