using Microsoft.AspNetCore.Server.Kestrel.Core;
using ToolCallsInspector.Core;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Ingestion.Otlp;
using ToolCallsInspector.Web.Components;
using ToolCallsInspector.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var databasePath = builder.Configuration["Inspector:DatabasePath"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolCallsInspector", "inspector.db");
}

builder.WebHost.ConfigureKestrel(options =>
{
    // Content capture can make OTLP batches large.
    options.Limits.MaxRequestBodySize = 256L * 1024 * 1024;
});

builder.Services.AddToolCallsInspectorCore(
    databasePath,
    builder.Configuration["Inspector:CopilotSessionStatePath"],
    builder.Configuration["Inspector:ClaudeProjectsPath"],
    builder.Configuration["Inspector:ClaudeApiBodiesPath"]);
builder.Services.AddSingleton(new InspectorOptions(databasePath));
builder.Services.AddSingleton<DemoDataService>();
builder.Services.AddSingleton<ViewPreferences>();
builder.Services.AddHostedService<TelemetrySourceHost>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

await app.Services.GetRequiredService<ISpanStore>().EnsureCreatedAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapOtlpReceiver();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
