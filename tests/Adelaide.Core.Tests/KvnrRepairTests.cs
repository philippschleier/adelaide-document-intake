using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>Repairing the common OCR misread of a KVNR's leading LETTER as a look-alike digit (e.g. an AOK 'O' read
/// as '0'). Synthetic KVNRs only — O123456780 is a constructed checksum-valid number, not a real one.</summary>
public class KvnrRepairTests
{
    [Fact] public void Leading_O_read_as_zero_is_repaired()
        => Assert.Equal("O123456780", Kvnr.Repair("0123456780"));

    [Fact] public void Repaired_value_is_checksum_valid()
        => Assert.True(Kvnr.IsValid(Kvnr.Repair("0123456780")));

    [Fact] public void Already_letter_leading_returns_null() // the caller checks IsValid first; Repair is only for the misread case
    {
        Assert.Null(Kvnr.Repair("Z000000005"));
        Assert.Null(Kvnr.Repair("A000000040"));
    }

    [Fact] public void Unrepairable_or_malformed_returns_null()
    {
        Assert.Null(Kvnr.Repair("0123456781")); // no leading look-alike yields a valid checksum
        Assert.Null(Kvnr.Repair("012345678X")); // non-digit body
        Assert.Null(Kvnr.Repair("123"));        // wrong length
        Assert.Null(Kvnr.Repair(null));
        Assert.Null(Kvnr.Repair(""));
    }
}
