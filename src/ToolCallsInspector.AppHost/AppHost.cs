var builder = DistributedApplication.CreateBuilder(args);

// Fixed, non-proxied ports: Copilot clients are configured to export to http://localhost:4318 and the
// MCP config points at http://localhost:5190/mcp, so these must not change between runs.
// The inspector's "Ui" and "Otlp" endpoints come from Kestrel:Endpoints in its appsettings.json.
builder.AddProject<Projects.ToolCallsInspector_Web>("inspector", launchProfileName: null)
    // Without a launch profile no environment is set; Development is required to serve un-published static web assets (scoped CSS).
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName)
    .WithEndpoint("Ui", e => e.IsProxied = false)
    .WithEndpoint("Otlp", e => e.IsProxied = false)
    .WithUrlForEndpoint("Ui", url => url.DisplayText = "Token dashboard")
    .WithUrlForEndpoint("Otlp", url =>
    {
        url.DisplayText = "OTLP receiver";
        url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
    })
    .WithHttpHealthCheck("/setup", endpointName: "Ui");

builder.AddProject<Projects.WeatherApi>("weather-api", launchProfileName: null)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName)
    .WithHttpEndpoint(port: 5190, name: "http", isProxied: false)
    .WithUrlForEndpoint("http", url => url.DisplayText = "Weather API")
    .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation { Url = "/api/weather/today", DisplayText = "Today's weather" })
    .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation { Url = "/mcp", DisplayText = "MCP endpoint", DisplayLocation = UrlDisplayLocation.DetailsOnly })
    .WithHttpHealthCheck("/", endpointName: "http");

builder.Build().Run();
