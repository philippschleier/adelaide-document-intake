using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace Adelaide.Web.Tests;

/// <summary>
/// DEMO MODE, proven against the real host rather than asserted: a child Adelaide.Web is started with
/// ADELAIDE_DEMO_MODE=1 on the synthetic corpus in demo/, real PDFs are uploaded through the real /api/upload, and the
/// documents must reach the documented end states through the unchanged UploadProcessor, gate, ledger and outbox.
/// Same isolation as the HTTP-surface harness: loopback-only ephemeral port, an explicit environment allowlist (no
/// developer credentials can leak in), and a throwaway state directory per run.
/// </summary>
public sealed class DemoModeTests
{
    [Fact]
    public void Production_settings_are_detected_by_name()
    {
        var env = new Dictionary<string, string?> { ["ENTRA_CLIENT_SECRET"] = "x", ["ADELAIDE_SQL_SERVER"] = "y", ["ADELAIDE_INBOX"] = "z" };
        Assert.Equal(new[] { "ADELAIDE_SQL_SERVER", "ENTRA_CLIENT_SECRET" },
            DemoMode.PresentProductionSettings(k => env.GetValueOrDefault(k)).Order());
        Assert.Empty(DemoMode.PresentProductionSettings(_ => null));
    }

    [Fact]
    public async Task Demo_host_refuses_to_start_beside_a_production_setting()
    {
        await using var host = await DemoHost.StartAsync(extraEnv: new() { ["ENTRA_CLIENT_SECRET"] = "synthetic-not-a-real-secret" }, waitHealthy: false);
        Assert.True(host.Process.WaitForExit(60_000), "demo host did not exit");
        Assert.NotEqual(0, host.Process.ExitCode);
        Assert.Contains("production settings are present: ENTRA_CLIENT_SECRET", host.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synthetic_samples_flow_through_the_real_pipeline_to_their_documented_end_states()
    {
        await using var host = await DemoHost.StartAsync();

        using var form = new MultipartFormDataContent();
        foreach (var f in new[] { "01-verordnung-mustermann.pdf", "05-arztbrief-testfall-namensgleich.pdf", "08-unleserliches-fax.pdf" })
        {
            var part = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(DemoHost.DemoDir, "samples", f)));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(part, "files", f);
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/upload") { Content = form };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var up = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        var states = await host.WaitForStatesAsync(expectedCount: 3, terminal: ["Filed", "NeedsReview", "DeadLetter"]);
        Assert.Equal("Filed", states["01-verordnung-mustermann.pdf"]);
        Assert.Equal("NeedsReview", states["05-arztbrief-testfall-namensgleich.pdf"]);
        Assert.Equal("NeedsReview", states["08-unleserliches-fax.pdf"]);

        // The auto-filed document is in the LOCAL library, not anywhere else.
        Assert.Single(Directory.GetFiles(Path.Combine(host.Root, "filed"), "*.pdf", SearchOption.AllDirectories));

        // The review patient picker reads the synthetic directory.
        using var patients = JsonDocument.Parse(await host.Client.GetStringAsync("/api/patients?q=Testfall"));
        Assert.Equal(2, patients.RootElement.GetArrayLength());

        // And the hash-chained ledger is intact after real processing.
        using var verify = JsonDocument.Parse(await host.Client.GetStringAsync("/api/audit/verify"));
        Assert.True(verify.RootElement.GetProperty("intact").GetBoolean());
    }

    private sealed class DemoHost : IAsyncDisposable
    {
        internal static readonly string DemoDir = FindDemoDir();
        public required Process Process { get; init; }
        public required HttpClient Client { get; init; }
        public required string Root { get; init; }
        private readonly System.Text.StringBuilder _log = new();
        public string Log { get { lock (_log) return _log.ToString(); } }

        public static async Task<DemoHost> StartAsync(Dictionary<string, string>? extraEnv = null, bool waitHealthy = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "adelaide-demo-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var port = FreePort();
            if (Array.IndexOf(HttpSurfaceFixture.ForbiddenPorts, port) >= 0) throw new InvalidOperationException($"refusing port {port}");

            var psi = new ProcessStartInfo(TestHostPaths.Dotnet(), $"\"{TestHostPaths.WebDll()}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
            };
            var keep = new[] { "PATH", "SystemRoot", "windir", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "HOME", "LOCALAPPDATA", "APPDATA" };
            foreach (var k in psi.Environment.Keys.Where(k => Array.IndexOf(keep, k) < 0).ToList()) psi.Environment.Remove(k);
            psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(TestHostPaths.Dotnet())!;
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; // the documented demo identity path (dev actor)
            psi.Environment["ADELAIDE_DEV_ACTOR"] = "demo-e2e";
            psi.Environment["ADELAIDE_DEMO_MODE"] = "1";
            psi.Environment["ADELAIDE_DEMO_DATA"] = DemoDir;
            psi.Environment["ADELAIDE_DEMO_FILED"] = Path.Combine(root, "filed");
            psi.Environment["ADELAIDE_AUDIT_DB"] = Path.Combine(root, "audit.db");
            psi.Environment["ADELAIDE_INBOX"] = Path.Combine(root, "inbox");
            psi.Environment["ADELAIDE_LOG_DIR"] = Path.Combine(root, "logs");
            foreach (var (k, v) in extraEnv ?? []) psi.Environment[k] = v;

            var host = new DemoHost
            {
                Process = new Process { StartInfo = psi },
                Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) },
                Root = root,
            };
            host.Process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (host._log) host._log.AppendLine(e.Data); };
            host.Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (host._log) host._log.AppendLine(e.Data); };
            host.Process.Start();
            host.Process.BeginOutputReadLine();
            host.Process.BeginErrorReadLine();
            if (!waitHealthy) return host;

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (host.Process.HasExited) throw new InvalidOperationException($"demo host exited ({host.Process.ExitCode}):\n{host.Log}");
                try { if ((await host.Client.GetAsync("/health")).IsSuccessStatusCode) return host; }
                catch (HttpRequestException) { /* not listening yet */ }
                await Task.Delay(250);
            }
            throw new InvalidOperationException($"demo host never became healthy:\n{host.Log}");
        }

        public async Task<Dictionary<string, string>> WaitForStatesAsync(int expectedCount, string[] terminal)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            Dictionary<string, string> states = new();
            while (DateTime.UtcNow < deadline)
            {
                using var doc = JsonDocument.Parse(await Client.GetStringAsync("/api/documents?scope=all"));
                states = doc.RootElement.EnumerateArray().ToDictionary(
                    d => d.GetProperty("original").GetString() ?? "", d => d.GetProperty("state").GetString() ?? "");
                if (states.Count >= expectedCount && states.Values.All(s => terminal.Contains(s))) return states;
                await Task.Delay(250);
            }
            throw new TimeoutException($"documents did not settle: {string.Join(", ", states.Select(kv => $"{kv.Key}={kv.Value}"))}\n{Log}");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try { if (!Process.HasExited) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            Process.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static string FindDemoDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "demo", "fixtures.json"))) return Path.Combine(dir.FullName, "demo");
            throw new DirectoryNotFoundException("demo/fixtures.json not found above the test output directory");
        }
    }
}
