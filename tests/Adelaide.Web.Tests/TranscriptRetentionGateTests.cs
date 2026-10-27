using Xunit;

namespace Adelaide.Web.Tests;

// The host-side configuration surface for the capture store's DELETION WINDOW (docs/CAPTURE.md). Capture itself is
// unconditional and not configurable — the old write-time gating (retention days as master switch + sample rate)
// is retired; ADELAIDE_TRANSCRIPT_SAMPLE_RATE is ignored entirely. What this asserts is the deletion posture the
// build ships with: no window by default, so nothing is ever aged out without an explicit decision — and the
// window's actual execution is additionally held by the PurgeGate kill-switch (tested via the UploadProcessor).
public class TranscriptRetentionGateTests
{
    [Fact]
    public void No_purge_window_by_default()
        => Assert.False(TranscriptRetentionGate.Enabled,
            "ADELAIDE_TRANSCRIPT_RETENTION_DAYS must default to NO WINDOW — a deletion schedule needs an explicit decision.");

    [Fact]
    public void The_default_policy_has_no_window()
        => Assert.Equal(0, TranscriptRetentionGate.Policy.RetentionDays);

    // Capture always writes, so the store file exists regardless; the path must still resolve beside the audit DB
    // so the captured PHI inherits the same volume/residency boundary.
    [Fact]
    public void The_store_path_defaults_beside_the_audit_database()
    {
        var p = TranscriptRetentionGate.StorePath(Path.Combine("C:", "data", "adelaide-audit.db"));
        Assert.Equal("adelaide-transcripts.db", Path.GetFileName(p));
    }

    // ---- the env-var -> policy mapping, with arbitrary inputs (the static above reads the environment only once) ----

    // A deployed one-year value now means: purge captured content older than a
    // year, once the PurgeGate also allows it. It no longer influences what is written.
    [Fact]
    public void A_configured_window_parses_to_days()
    {
        var p = TranscriptRetentionGate.PolicyFrom("365");
        Assert.Equal(365, p.RetentionDays);
        Assert.True(p.Enabled);
    }

    // Unparseable or out-of-range values must fail to the NO-WINDOW end: a typo'd window must never silently
    // become an enormous one — and with capture unconditional, the safe default for DELETION is "don't".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    public void Malformed_configuration_means_no_window(string? days)
        => Assert.False(TranscriptRetentionGate.PolicyFrom(days).Enabled);

    [Fact]
    public void The_banner_line_states_the_mode()
        => Assert.Contains("capture=always", TranscriptRetentionGate.Describe(), StringComparison.Ordinal);

    // ---- C3 substrate selection (the policy the C1 guard evolved into; pure mapping, arbitrary inputs) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cloud_mode_without_an_override_selects_the_sql_store(string? env)
    {
        var (azureSql, path) = TranscriptRetentionGate.SelectSubstrate(cloudMode: true, env, "adelaide-audit.db");
        Assert.True(azureSql);
        Assert.Null(path);
    }

    [Fact]
    public void Cloud_mode_with_an_explicit_override_is_the_sqlite_escape_hatch()
    {
        var (azureSql, path) = TranscriptRetentionGate.SelectSubstrate(cloudMode: true, "/mnt/data/t.db", "adelaide-audit.db");
        Assert.False(azureSql);
        Assert.Equal("/mnt/data/t.db", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")] // a whitespace-only override must never become a literal path (verification finding)
    public void Local_mode_defaults_beside_the_audit_database(string? env)
    {
        var (azureSql, path) = TranscriptRetentionGate.SelectSubstrate(cloudMode: false, env, Path.Combine("C:", "data", "adelaide-audit.db"));
        Assert.False(azureSql);
        Assert.Equal("adelaide-transcripts.db", Path.GetFileName(path!));
    }

    // ---- C1 restored, narrowly: in a container a file-path capture store is a FATAL startup error ----
    // The misconfiguration it exists for is the SQL settings being ABSENT — unset, blank, or supplied under a
    // misspelled setting NAME: cloud mode does not resolve, capture silently falls back to SQLite on ephemeral
    // container disk, and every write is fail-open-but-counted, so the loss is invisible until someone asks the
    // store a question. NOT a mistyped server VALUE — ConnectionStringFromEnv only checks for blankness, so that
    // still resolves cloud mode and fails later as a SQL error (adversarial review corrected this rationale).

    [Fact]
    public void In_the_container_a_file_path_capture_store_is_fatal_and_names_what_to_fix()
    {
        var err = TranscriptRetentionGate.DurabilityError(inContainer: true, azureSql: false,
            resolvedPath: "/app/adelaide-transcripts.db", transcriptDbEnv: null);
        Assert.NotNull(err);
        Assert.Contains("/app/adelaide-transcripts.db", err, StringComparison.Ordinal); // the resolved path
        Assert.Contains("SQLite", err, StringComparison.Ordinal);                        // the resolved substrate
        Assert.Contains("ADELAIDE_SQL_SERVER", err, StringComparison.Ordinal);           // the variable to fix
    }

    [Fact]
    public void In_the_container_an_explicit_override_is_fatal_too_and_names_that_variable_instead()
    {
        var err = TranscriptRetentionGate.DurabilityError(inContainer: true, azureSql: false,
            resolvedPath: "/app/t.db", transcriptDbEnv: "/app/t.db");
        Assert.NotNull(err);
        Assert.Contains("ADELAIDE_TRANSCRIPT_DB", err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_container_guard_passes_on_the_azure_sql_twin()
        => Assert.Null(TranscriptRetentionGate.DurabilityError(inContainer: true, azureSql: true, resolvedPath: "AzureSql", transcriptDbEnv: null));

    [Fact]
    public void The_container_guard_never_fires_off_container()
        // Localhost IS a file-path capture store, by design — the guard must not touch it.
        => Assert.Null(TranscriptRetentionGate.DurabilityError(inContainer: false, azureSql: false,
            resolvedPath: @"C:\data\adelaide-transcripts.db", transcriptDbEnv: null));

    [Theory]
    [InlineData("1", null, true)]
    [InlineData(null, "true", true)]    // the aspnet base image's own marker, so an image predating ours is covered
    [InlineData(null, "True", true)]
    [InlineData(null, null, false)]
    [InlineData("0", "false", false)]
    public void Container_detection_accepts_either_marker(string? adelaide, string? dotnet, bool expected)
        => Assert.Equal(expected, TranscriptRetentionGate.InContainer(adelaide, dotnet));

    // ---- the escape-hatch WARN: it must fire on the SET variable, not on cloud mode ----
    // It was guarded by `sqlConn != null`, so the case that most needs announcing — an explicit override while the
    // intended cloud substrate did not resolve — produced no line at all.

    [Fact]
    public void The_escape_hatch_warns_without_a_sql_connection()
        => Assert.True(TranscriptRetentionGate.EscapeHatchActive("/mnt/data/t.db"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_override_is_not_an_escape_hatch(string? env)
        => Assert.False(TranscriptRetentionGate.EscapeHatchActive(env));
}
