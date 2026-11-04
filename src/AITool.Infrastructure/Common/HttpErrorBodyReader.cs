using System.Text;

namespace AITool.Infrastructure.Common;

/// <summary>Reads a small diagnostic prefix without letting an error response stall forwarding.</summary>
internal static class HttpErrorBodyReader
{
    private const int MaxBytes = 8 * 1024;

    public static async Task<string> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(1));
        var bytes = new byte[MaxBytes];
        var count = 0;
        try
        {
            using var stream = await content.ReadAsStreamAsync(budget.Token).WaitAsync(budget.Token);
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), budget.Token).AsTask().WaitAsync(budget.Token);
                if (read == 0) break;
                count += read;
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        catch (IOException) { cancellationToken.ThrowIfCancellationRequested(); }
        catch (HttpRequestException) { cancellationToken.ThrowIfCancellationRequested(); }
        cancellationToken.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
}
