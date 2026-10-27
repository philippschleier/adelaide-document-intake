using Xunit;

namespace Adelaide.Web.Tests;

// FINDING 3: cryptographic principal validation is OFF unless it is explicitly requested AND a tenant + client id are
// configured (there must be something to validate against). This gate keeps the default deployment on the unchanged
// header path. The token-signature validation itself needs a live Entra JWKS and is covered by integration testing.
public class EasyAuthValidationTests
{
    [Theory]
    [InlineData(null, "tenant", "client", false)]   // flag unset → off (default)
    [InlineData("true", "tenant", "client", true)]  // fully configured → on
    [InlineData("TRUE", "tenant", "client", true)]  // case-insensitive
    [InlineData("false", "tenant", "client", false)]// explicit opt-out
    [InlineData("true", null, "client", false)]     // no tenant → nothing to validate against
    [InlineData("true", "tenant", null, false)]     // no client id → no audience to check
    [InlineData("true", "  ", "client", false)]     // blank tenant → off
    public void ResolveEnabled_requires_flag_tenant_and_clientId(string? flag, string? tenant, string? clientId, bool expected) =>
        Assert.Equal(expected, EasyAuthValidation.ResolveEnabled(flag, tenant, clientId));
}
