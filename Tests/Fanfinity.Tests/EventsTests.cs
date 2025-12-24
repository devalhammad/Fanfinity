namespace Fanfinity.Tests;

public class MetricsServiceTests
{
    [Fact]
    public void RecordRequest_ShouldIncrementTotalRequests()
    {
        var service = new MetricsService();

        service.RecordRequest(100, "POST", "/api/events", 202);

        var metrics = service.GetPrometheusMetrics();
        Assert.Contains("http_requests_total 1", metrics);
    }

    [Fact]
    public void RecordRequest_ShouldTrackErrors()
    {
        var service = new MetricsService();

        service.RecordRequest(100, "POST", "/api/events", 500);

        var metrics = service.GetPrometheusMetrics();
        Assert.Contains("http_errors_total 1", metrics);
    }


    [Fact]
    public void RecordRequest_ShouldTrackEventsProcessed()
    {
        var service = new MetricsService();

        service.RecordRequest(50, "POST", "/api/events", 202);
        service.RecordRequest(60, "POST", "/api/events", 202);

        var metrics = service.GetPrometheusMetrics();
        Assert.Contains("events_processed_total 2", metrics);
    }

    [Fact]
    public void GetMetrics_ShouldReturnPercentiles()
    {
        var service = new MetricsService();

        service.RecordRequest(100, "GET", "/test", 200);
        service.RecordRequest(200, "GET", "/test", 200);
        service.RecordRequest(300, "GET", "/test", 200);

        var metrics = service.GetMetrics();

        // Use reflection to access anonymous type properties
        var p50 = (double)metrics.GetType().GetProperty("p50")!.GetValue(metrics)!;
        var p95 = (double)metrics.GetType().GetProperty("p95")!.GetValue(metrics)!;
        var p99 = (double)metrics.GetType().GetProperty("p99")!.GetValue(metrics)!;

        Assert.True(p50 > 0);
        Assert.True(p95 > 0);
        Assert.True(p99 > 0);
    }

}