using MevzuatUygunluk.Models;

namespace MevzuatUygunluk.Services;

public interface IFeedbackStore
{
    Task AddAsync(FeedbackItem item, CancellationToken ct = default);
    Task<List<FeedbackItem>> LoadAllAsync(CancellationToken ct = default);
    Task<List<FeedbackItem>> LoadForAsync(string scenario, string invoiceType, CancellationToken ct = default);
}
