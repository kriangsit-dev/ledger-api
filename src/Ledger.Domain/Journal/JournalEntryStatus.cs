namespace Ledger.Domain.Journal;

public enum JournalEntryStatus
{
    /// <summary>Recorded and counted in every balance.</summary>
    Posted = 1,

    /// <summary>Cancelled by a later, mirrored entry. Still counted — the reversal is what cancels it.</summary>
    Reversed = 2,
}
