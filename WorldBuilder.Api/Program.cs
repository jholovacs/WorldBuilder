using System.Text.Json.Serialization;
using WorldBuilder.Api.Storage;
using WorldBuilder.Api.Terrain;
using WorldBuilder.Api.Hubs;
using WorldBuilder.Domain.Terrain;
using WorldBuilder.Gpu;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(static options =>
{
    options.Limits.MaxRequestBodySize = 20 * 1024 * 1024;
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.Configure<WorldStorageOptions>(builder.Configuration.GetSection(WorldStorageOptions.SectionName));
builder.Services.Configure<TerrainMapsOptions>(builder.Configuration.GetSection(TerrainMapsOptions.SectionName));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IWorldDefinitionStore, FileWorldDefinitionStore>();
builder.Services.AddScoped<ITerrainMapStore, FileTerrainMapStore>();
builder.Services.AddSingleton<TerrainPreviewSessionStore>();
builder.Services.AddSingleton<TerrainExportService>();
builder.Services.AddSingleton<ITerrainGpuCompute>(_ =>
    TerrainVulkanGpuCompute.TryCreate() ?? (ITerrainGpuCompute)NullTerrainGpuCompute.Instance);
builder.Services.AddSingleton<TerrainGenerationCoordinator>();
builder.Services.AddScoped<TerrainGenerationService>();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",
                "https://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("AngularDev");
app.UseAuthorization();
app.MapControllers();
app.MapHub<WorldProgressHub>("/hubs/world-progress");
app.MapHub<TerrainGenerationHub>("/hubs/terrain-generation");

app.Run();
