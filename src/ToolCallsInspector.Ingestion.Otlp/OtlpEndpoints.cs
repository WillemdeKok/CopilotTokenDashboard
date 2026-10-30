using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToolCallsInspector.Core.Ingestion;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Ingestion.Otlp;

public static class OtlpEndpoints
{
    public const string SourceName = "otlp-http";
    private const long MaxPayloadBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Maps the OTLP/HTTP receiver: <c>POST /v1/traces</c> is ingested; <c>/v1/logs</c> and <c>/v1/metrics</c>
    /// are acknowledged so exporters don't log failures (spans already carry all required data).
    /// </summary>
    public static IEndpointRouteBuilder MapOtlpReceiver(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").DisableAntiforgery();

        group.MapPost("/traces", HandleTracesAsync);
        group.MapPost("/logs", AcknowledgeAsync);
        group.MapPost("/metrics", AcknowledgeAsync);
        return endpoints;
    }

    private static async Task HandleTracesAsync(HttpContext context, ITelemetryIngestor ingestor, IngestionStatus status, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(OtlpEndpoints));
        var isProtobuf = IsProtobuf(context.Request.ContentType);
        var source = $"{SourceName} ({(isProtobuf ? "protobuf" : "json")})";

        IReadOnlyList<RawSpan> spans;
        try
        {
            var payload = await ReadBodyAsync(context.Request, context.RequestAborted);
            spans = isProtobuf ? OtlpProtobufTraceParser.Parse(payload) : OtlpJsonTraceParser.Parse(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rejected malformed OTLP trace payload");
            status.Record(source, 0, $"Malformed payload: {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Malformed OTLP payload", context.RequestAborted);
            return;
        }

        await ingestor.IngestAsync(spans, source, context.RequestAborted);
        await WriteSuccessAsync(context, isProtobuf);
    }

    private static async Task AcknowledgeAsync(HttpContext context)
    {
        // Drain the body so keep-alive connections stay healthy.
        await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
        await WriteSuccessAsync(context, IsProtobuf(context.Request.ContentType));
    }

    private static async Task WriteSuccessAsync(HttpContext context, bool protobuf)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        if (protobuf)
        {
            // An empty Export*ServiceResponse serializes to zero bytes.
            context.Response.ContentType = "application/x-protobuf";
            context.Response.ContentLength = 0;
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{}", context.RequestAborted);
    }

    private static bool IsProtobuf(string? contentType) =>
        contentType is not null && contentType.Contains("protobuf", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        Stream body = request.Body;
        var encoding = request.Headers.ContentEncoding.ToString();
        if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            body = new GZipStream(body, CompressionMode.Decompress);
        }
        else if (encoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
        {
            body = new ZLibStream(body, CompressionMode.Decompress);
        }

        await using (body)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxPayloadBytes)
                {
                    throw new InvalidDataException("OTLP payload exceeds maximum size");
                }
            }

            return buffer.ToArray();
        }
    }
}
