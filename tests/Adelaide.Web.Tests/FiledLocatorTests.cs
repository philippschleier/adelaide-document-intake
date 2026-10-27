using Adelaide.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// Stage III tripwire: FiledLocator.Require returns the driveItemId for a filed doc; for a doc WITHOUT one — an invariant
// violation, since Phase 2a drove class (c) to 0 and the write path can't produce an id-less Filed event — it returns
// null and logs an ERROR, so the caller fails clean and NEVER falls back to the filename.
public class FiledLocatorTests
{
    // Filename embeds a (synthetic) KVNR so we can assert the tripwire log does NOT leak it (no name fallback).
    private static DocumentAudit Doc(string state, string? driveItemId) =>
        new("doc1", state, null, "2026-07-02_Verordnung_Z000000005_doc1.pdf", "Verordnung", null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, DriveItemId: driveItemId);

    [Fact]
    public void Returns_the_driveItemId_when_present()
    {
        Assert.Equal("drv-1", FiledLocator.Require(Doc("Filed", "drv-1"), "api/file", NullLogger.Instance));
    }

    [Fact]
    public void Tripwire_returns_null_and_logs_ERROR_when_no_driveItemId()
    {
        var log = new CapturingLogger();
        Assert.Null(FiledLocator.Require(Doc("Filed", null), "api/file", log));

        var err = Assert.Single(log.Entries.Where(e => e.Level == LogLevel.Error));
        Assert.Contains("driveItemId", err.Message);
        Assert.Contains("invariant", err.Message);
        Assert.DoesNotContain("Z000000005", err.Message); // never leaks the filename — no name fallback, no PHI
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
