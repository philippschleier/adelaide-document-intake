using System.Net;
using System.Text;
using Adelaide.Core;
using Adelaide.SharePoint;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.SharePoint.Tests;

public class GraphPatientDirectoryTests
{
    // ---- helpers ----
    private static SharePointOptions Options() => new("tenant", "client", "secret", "host,site,web");
    private static ExtractedData KvnrData(string kvnr) => new(new[] { new ExtractedField("KVNR", kvnr, 1.0, "test") });
    private static ExtractedData NameDob(string last, string dob) =>
        new(new[] { new ExtractedField("LastName", last, 1.0, "test"), new ExtractedField("BirthDate", dob, 1.0, "test") });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string PatientsList = "{\"value\":[{\"id\":\"L1\",\"displayName\":\"Patients\"}]}";

    private static GraphPatientDirectory Direct(StubHandler h) =>
        new(new HttpClient(h), Options(), new FakeCredential(), NullLogger<GraphPatientDirectory>.Instance);

    // ---- orchestration ----
    [Fact]
    public async Task MatchAsync_by_kvnr_returns_single()
    {
        var h = new StubHandler(req => req.RequestUri!.ToString().Contains("/items")
            ? Json("{\"value\":[{\"id\":\"9\",\"fields\":{\"InsuranceNumber\":\"Z000000005\",\"PatientLastName\":\"Mustermann\",\"PatientStatus\":\"Active\"}}]}")
            : Json(PatientsList));

        var m = await Direct(h).MatchAsync(KvnrData("Z000000005"));

        Assert.Equal(MatchOutcome.Single, m.Outcome);
        Assert.Equal(9, m.Patient!.ListItemId);
        Assert.Equal("Mustermann", m.Patient.LastName);
    }

    [Fact]
    public async Task MatchAsync_name_dob_filters_to_the_matching_birthdate()
    {
        var h = new StubHandler(req => req.RequestUri!.ToString().Contains("/items")
            ? Json("{\"value\":[" +
                   "{\"id\":\"1\",\"fields\":{\"PatientLastName\":\"Müller\",\"DateOfBirth\":\"1950-01-01T00:00:00Z\",\"PatientStatus\":\"Active\"}}," +
                   "{\"id\":\"2\",\"fields\":{\"PatientLastName\":\"Müller\",\"DateOfBirth\":\"1956-05-29T00:00:00Z\",\"PatientStatus\":\"Active\"}}]}")
            : Json(PatientsList));

        var m = await Direct(h).MatchAsync(NameDob("Müller", "1956-05-29"));

        Assert.Equal(MatchOutcome.Single, m.Outcome);
        Assert.Equal(2, m.Patient!.ListItemId);
    }

    // ---- (b) paging: the match is on page 2; must not be truncated ----
    [Fact]
    public async Task MatchAsync_follows_paging_and_does_not_truncate()
    {
        var h = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("PAGE2"))
                return Json("{\"value\":[{\"id\":\"2\",\"fields\":{\"PatientLastName\":\"Müller\",\"DateOfBirth\":\"1956-05-29T00:00:00Z\",\"PatientStatus\":\"Active\"}}]}");
            if (url.Contains("/items"))
                return Json("{\"value\":[{\"id\":\"1\",\"fields\":{\"PatientLastName\":\"Müller\",\"DateOfBirth\":\"1950-01-01T00:00:00Z\"}}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/PAGE2\"}");
            return Json(PatientsList);
        });

        var m = await Direct(h).MatchAsync(NameDob("Müller", "1956-05-29"));

        Assert.Equal(MatchOutcome.Single, m.Outcome);
        Assert.Equal(2, m.Patient!.ListItemId); // only reachable if page 2 was fetched
    }

    // ---- transport error ----
    [Fact]
    public async Task Non_success_response_throws()
    {
        var h = new StubHandler(req => req.RequestUri!.ToString().Contains("/items")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
            : Json(PatientsList));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Direct(h).MatchAsync(KvnrData("Z000000005")));
    }

    // ---- (a) resilience: 429 is retried, then succeeds ----
    [Fact]
    public async Task Transient_429_is_retried_then_succeeds()
    {
        int itemsCalls = 0;
        var h = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/items"))
            {
                itemsCalls++;
                return itemsCalls == 1
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    : Json("{\"value\":[{\"id\":\"5\",\"fields\":{\"InsuranceNumber\":\"Z000000005\",\"PatientStatus\":\"Active\"}}]}");
            }
            return Json(PatientsList);
        });

        var services = new ServiceCollection();
        services.AddSingleton(Options());
        services.AddSingleton<TokenCredential>(new FakeCredential());
        services.AddLogging();
        services.AddHttpClient<GraphPatientDirectory>()
            .ConfigurePrimaryHttpMessageHandler(() => h)
            .AddStandardResilienceHandler(o => { o.Retry.Delay = TimeSpan.FromMilliseconds(1); o.Retry.UseJitter = false; });
        using var sp = services.BuildServiceProvider();

        var m = await sp.GetRequiredService<GraphPatientDirectory>().MatchAsync(KvnrData("Z000000005"));

        Assert.Equal(MatchOutcome.Single, m.Outcome);
        Assert.True(itemsCalls >= 2, $"expected a retry; items endpoint called {itemsCalls}x");
    }

    // ---- test doubles ----
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
            new(GetToken(c, ct));
    }
}
