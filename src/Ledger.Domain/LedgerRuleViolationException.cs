namespace Ledger.Domain;

/// <summary>
/// Raised when an operation would break an accounting invariant. Callers get 422 for these:
/// the request was well-formed, but the ledger refuses to record it.
/// </summary>
public sealed class LedgerRuleViolationException(string message) : Exception(message);
