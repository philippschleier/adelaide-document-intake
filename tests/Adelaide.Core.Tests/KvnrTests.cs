using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

public class KvnrTests
{
    [Theory]
    [InlineData("Z000000005")]  // synthetic (check digit 5)
    [InlineData("T000000002")]  // synthetic (check digit 2)
    [InlineData("z000000005")]  // lowercase is normalised
    public void Valid_kvnrs_pass(string kvnr) => Assert.True(Kvnr.IsValid(kvnr));

    [Theory]
    [InlineData("Z000000004")]  // wrong check digit
    [InlineData("Z00000000")]   // too short
    [InlineData("Z0000000051")] // too long
    [InlineData("1234567890")]  // no leading letter
    [InlineData("Z00000000X")]  // non-digit in body
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_kvnrs_fail(string? kvnr) => Assert.False(Kvnr.IsValid(kvnr));
}
