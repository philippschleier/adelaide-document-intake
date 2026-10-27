using Microsoft.AspNetCore.Http;
using Xunit;

namespace Adelaide.Web.Tests;

// FINDING 2: the CSRF endpoint filter must reject a mutation that arrives without the custom header (a cross-site form
// or simple request cannot set one) and pass a request that carries it (our same-origin SPA does, via the interceptor).
public class CsrfHeaderFilterTests
{
    private const string Reached = "reached-next";

    private static async Task<object?> InvokeAsync(string? headerValue)
    {
        var http = new DefaultHttpContext();
        if (headerValue is not null) http.Request.Headers[CsrfHeaderFilter.RequiredHeader] = headerValue;
        var ctx = EndpointFilterInvocationContext.Create(http);
        return await new CsrfHeaderFilter().InvokeAsync(ctx, _ => ValueTask.FromResult<object?>(Reached));
    }

    [Fact]
    public async Task Missing_header_is_rejected_with_403()
    {
        var result = await InvokeAsync(null);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task Blank_header_is_rejected_with_403()
    {
        var result = await InvokeAsync("   ");
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task Present_header_passes_through_to_the_endpoint()
    {
        var result = await InvokeAsync("XMLHttpRequest");
        Assert.Equal(Reached, result);
    }
}
