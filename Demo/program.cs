using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Demo.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

// Add CORS for local development (adjust for production)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Register DbContext
builder.Services.AddDbContext<RevolutionContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// Run migrations and import (dev)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RevolutionContext>();
    try
    {
        db.Database.Migrate();

        // Import from Wikidata (best effort)
        try
        {
            await WikidataImporter.FetchAndImportAsync(db);
        }
        catch (Exception ex)
        {
            // log import failure but don't crash startup
            Console.Error.WriteLine("Wikidata import failed: " + ex.Message);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Database migration failed: " + ex);
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Serve static files before routing so map assets load
app.UseStaticFiles();

// Routing & middleware
app.UseRouting();
app.UseCors(); // enable CORS (dev)
app.UseAuthorization();

// Diagnostics: quick health and route listing
app.MapGet("/ping", () => Results.Text("pong"));
app.MapGet("/routes", (EndpointDataSource ds) =>
{
    var list = ds.Endpoints
        .OfType<RouteEndpoint>()
        .Select(e => new { Pattern = e.RoutePattern.RawText, e.DisplayName })
        .OrderBy(e => e.Pattern)
        .ToList();
    return Results.Json(list);
});

// API endpoint for the client map (register before Razor Pages)
app.MapGet("/api/revolutions", async (RevolutionContext db) =>
{
    var items = await db.Revolutions
        .Where(r => r.StartDate.Year >= 1900)
        .Select(r => new {
            r.Id,
            r.Name,
            r.StartDate,
            r.EndDate,
            r.Country,
            r.Latitude,
            r.Longitude,
            r.Type,
            r.Description,
            r.WikidataId
        })
        .ToListAsync();
    return Results.Ok(items);
});

// Map Razor Pages last
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
