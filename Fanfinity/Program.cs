using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.MapHealthChecks("/health");

var metrics = app.Services.GetRequiredService<MetricsService>();

// POST /api/events
app.MapPost("/api/events", async (
    [FromBody] MatchEvent matchEvent,
    IConnectionMultiplexer redis,
    MetricsService metricsService) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        if (string.IsNullOrEmpty(matchEvent.MatchId) || string.IsNullOrEmpty(matchEvent.EventType))
            return Results.BadRequest(new { error = "MatchId and EventType are required" });

        matchEvent.EventId = Guid.NewGuid().ToString();
        matchEvent.Timestamp = DateTime.UtcNow;

        var db = redis.GetDatabase();

        // Store event
        var eventKey = $"event:{matchEvent.EventId}";
        await db.StringSetAsync(eventKey, JsonSerializer.Serialize(matchEvent));

        // Add to match events list
        await db.ListRightPushAsync($"match:{matchEvent.MatchId}:events", matchEvent.EventId);

        // Increment counters
        await db.StringIncrementAsync($"match:{matchEvent.MatchId}:total");
        await db.StringIncrementAsync($"match:{matchEvent.MatchId}:type:{matchEvent.EventType}");

        // Track events per minute
        var minuteKey = $"match:{matchEvent.MatchId}:minute:{DateTime.UtcNow:yyyyMMddHHmm}";
        await db.StringIncrementAsync(minuteKey);
        await db.KeyExpireAsync(minuteKey, TimeSpan.FromHours(24));

        sw.Stop();
        metricsService.RecordRequest(sw.ElapsedMilliseconds, "POST", "/api/events", 202);

        return Results.Accepted($"/api/events/{matchEvent.EventId}", new { eventId = matchEvent.EventId });
    }
    catch (Exception ex)
    {
        sw.Stop();
        metricsService.RecordRequest(sw.ElapsedMilliseconds, "POST", "/api/events", 500);
        return Results.Problem(ex.Message);
    }
});

// GET /api/matches/{matchId}/metrics
app.MapGet("/api/matches/{matchId}/metrics", async (
    string matchId,
    IConnectionMultiplexer redis,
    MetricsService metricsService) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        var db = redis.GetDatabase();

        var totalEvents = (long)(await db.StringGetAsync($"match:{matchId}:total"));

        // Get events by type
        var eventTypes = new[] { "goal", "yellow_card", "red_card", "substitution", "match_start", "match_end" };
        var eventsByType = new Dictionary<string, long>();

        foreach (var type in eventTypes)
        {
            var count = (long)(await db.StringGetAsync($"match:{matchId}:type:{type}"));
            if (count > 0) eventsByType[type] = count;
        }

        // Get peak engagement (events per minute for last hour)
        var peakEngagement = new List<PeakPeriod>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 60; i++)
        {
            var minute = now.AddMinutes(-i);
            var minuteKey = $"match:{matchId}:minute:{minute:yyyyMMddHHmm}";
            var count = (long)(await db.StringGetAsync(minuteKey));
            if (count > 0)
            {
                peakEngagement.Add(new PeakPeriod
                {
                    Timestamp = minute.ToString("yyyy-MM-ddTHH:mm:00Z"),
                    EventCount = count
                });
            }
        }

        var responseMetrics = metricsService.GetMetrics();

        sw.Stop();
        metricsService.RecordRequest(sw.ElapsedMilliseconds, "GET", $"/api/matches/{matchId}/metrics", 200);

        return Results.Ok(new
        {
            matchId,
            totalEvents,
            eventsByType,
            peakEngagementPeriods = peakEngagement.OrderByDescending(p => p.EventCount).Take(10),
            responseTimePercentiles = responseMetrics
        });
    }
    catch (Exception ex)
    {
        sw.Stop();
        metricsService.RecordRequest(sw.ElapsedMilliseconds, "GET", $"/api/matches/{matchId}/metrics", 500);
        return Results.Problem(ex.Message);
    }
});

// GET /metrics (Prometheus format)
app.MapGet("/metrics", (MetricsService metricsService) =>
{
    var metrics = metricsService.GetPrometheusMetrics();
    return Results.Content(metrics, "text/plain");
});

app.Run();

// Models
public record MatchEvent
{
    public string EventId { get; set; } = "";
    public string MatchId { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string? TeamId { get; set; }
    public string? PlayerId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public record PeakPeriod
{
    public string Timestamp { get; set; } = "";
    public long EventCount { get; set; }
}

// Metrics Service
public class MetricsService
{
    private readonly List<double> _responseTimes = new();
    private long _totalRequests = 0;
    private long _totalErrors = 0;
    private long _eventsProcessed = 0;
    private readonly object _lock = new();

    public void RecordRequest(double latencyMs, string method, string path, int statusCode)
    {
        lock (_lock)
        {
            _responseTimes.Add(latencyMs);
            if (_responseTimes.Count > 10000) _responseTimes.RemoveAt(0);

            _totalRequests++;
            if (statusCode >= 500) _totalErrors++;
            if (method == "POST" && path == "/api/events" && statusCode == 202) _eventsProcessed++;
        }
    }

    public object GetMetrics()
    {
        lock (_lock)
        {
            if (_responseTimes.Count == 0)
                return new { p50 = 0.0, p95 = 0.0, p99 = 0.0 };

            var sorted = _responseTimes.OrderBy(x => x).ToList();
            return new
            {
                p50 = GetPercentile(sorted, 0.50),
                p95 = GetPercentile(sorted, 0.95),
                p99 = GetPercentile(sorted, 0.99)
            };
        }
    }

    public string GetPrometheusMetrics()
    {
        lock (_lock)
        {
            var metrics = GetMetrics();
            var p50 = ((dynamic)metrics).p50;
            var p95 = ((dynamic)metrics).p95;
            var p99 = ((dynamic)metrics).p99;

            return $@"# HELP http_requests_total Total number of HTTP requests
# TYPE http_requests_total counter
http_requests_total {_totalRequests}

# HELP http_errors_total Total number of HTTP errors
# TYPE http_errors_total counter
http_errors_total {_totalErrors}

# HELP events_processed_total Total number of events processed
# TYPE events_processed_total counter
events_processed_total {_eventsProcessed}

# HELP http_request_duration_ms HTTP request latency in milliseconds
# TYPE http_request_duration_ms summary
http_request_duration_ms{{quantile=""0.5""}} {p50}
http_request_duration_ms{{quantile=""0.95""}} {p95}
http_request_duration_ms{{quantile=""0.99""}} {p99}
";
        }
    }

    private double GetPercentile(List<double> sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, index)];
    }
}