using Ledger.Domain;
using Ledger.Domain.Accounts;
using Ledger.Domain.Journal;

namespace Ledger.Api.Contracts;

public sealed record CreateAccountRequest(string Code, string Name, AccountType Type, string Currency);

public sealed record AccountResponse(
    Guid Id,
    string Code,
    string Name,
    AccountType Type,
    string Currency,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record JournalLineRequest(Guid AccountId, EntryDirection Direction, decimal Amount, string? Memo);

public sealed record PostJournalEntryRequest(
    string Reference,
    string Description,
    string Currency,
    DateOnly OccurredOn,
    IReadOnlyList<JournalLineRequest> Lines);

public sealed record ReverseJournalEntryRequest(string Reference, DateOnly? OccurredOn);

public sealed record JournalLineResponse(
    Guid Id,
    Guid AccountId,
    EntryDirection Direction,
    decimal Amount,
    string? Memo);

public sealed record JournalEntryResponse(
    Guid Id,
    string Reference,
    string Description,
    string Currency,
    DateOnly OccurredOn,
    DateTimeOffset PostedAt,
    JournalEntryStatus Status,
    Guid? ReversalOfEntryId,
    Guid? ReversedByEntryId,
    decimal TotalDebits,
    decimal TotalCredits,
    IReadOnlyList<JournalLineResponse> Lines);

public sealed record TokenRequest(string ClientId, string ClientSecret);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string Scope);

public static class ResponseMapper
{
    public static AccountResponse ToResponse(this Account account) =>
        new(
            account.Id,
            account.Code,
            account.Name,
            account.Type,
            account.Currency.Code,
            account.IsActive,
            account.CreatedAt);

    public static JournalEntryResponse ToResponse(this JournalEntry entry) =>
        new(
            entry.Id,
            entry.Reference,
            entry.Description,
            entry.Currency.Code,
            entry.OccurredOn,
            entry.PostedAt,
            entry.Status,
            entry.ReversalOfEntryId,
            entry.ReversedByEntryId,
            entry.TotalDebits.Amount,
            entry.TotalCredits.Amount,
            entry.Lines
                .Select(line => new JournalLineResponse(line.Id, line.AccountId, line.Direction, line.Amount, line.Memo))
                .ToList());
}
