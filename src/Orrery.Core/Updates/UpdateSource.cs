using System.Net;
using System.Net.Http.Headers;

namespace Orrery.Core.Updates;

/// <summary>
/// Reads the release list and release files, from GitHub (or any http(s) URL) or local files, with a size cap and
/// timeouts. Sends only a User-Agent naming Orrery's version and an Accept header: nothing about the user.
/// </summary>
sealed class UpdateSource : IDisposable
{
    /// <summary>How long a server has to start answering.</summary>
    static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a download may go without receiving anything before it's given up.</summary>
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    const int BufferSize = 81920;

    readonly HttpClient _http;

    public UpdateSource(ReleaseVersion current)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            ConnectTimeout = AnswerTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // Each request sets its own timeouts, so a long download isn't cut off part way.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Orrery", current.ToString()));
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Reads a small file (the release list, SHA256SUMS) as text.</summary>
    public async Task<string> ReadTextAsync(Uri url, string accept, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await CopyAsync(url, accept, maxBytes, null, (block, token) => buffer.WriteAsync(block, token), null, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Streams <paramref name="url"/> to <paramref name="write"/>, block by block, reporting the bytes received so
    /// far. Fails if it's larger than <paramref name="maxBytes"/>, or than <paramref name="expectedSize"/> when given.
    /// </summary>
    public async Task<long> CopyAsync(Uri url, string accept, long maxBytes, long? expectedSize,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var what = Describe(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            timeout.CancelAfter(AnswerTimeout);
            using var response = url.IsFile ? null : await GetAsync(url, accept, what, timeout.Token);
            await using var body = response is null
                ? new FileStream(url.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true)
                : await response.Content.ReadAsStreamAsync(timeout.Token);
            long? length = response is null ? body.Length : response.Content.Headers.ContentLength;
            long limit = Math.Min(maxBytes, expectedSize ?? maxBytes);
            if (length > limit)
            {
                throw new UpdateException($"{Path.GetFileName(url.LocalPath)} is larger than expected.");
            }

            var buffer = new byte[BufferSize];
            long total = 0;
            while (true)
            {
                timeout.CancelAfter(StallTimeout);
                int read = await body.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > limit)
                {
                    throw new UpdateException($"{Path.GetFileName(url.LocalPath)} is larger than expected.");
                }

                await write(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(total);
            }

            return total;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"{what} stopped answering.", e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"Couldn't reach {what}.", e);
        }
        catch (IOException e) when (url.IsFile)
        {
            throw new UpdateException($"Couldn't read {url.LocalPath}: {e.Message}", e);
        }
        catch (UnauthorizedAccessException e) when (url.IsFile)
        {
            throw new UpdateException($"Couldn't read {url.LocalPath}: {e.Message}", e);
        }
    }

    async Task<HttpResponseMessage> GetAsync(Uri url, string accept, string what, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new UpdateException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? $"{what} turned the request away for now ({(int)response.StatusCode}). Try again later."
                : $"{what} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return response;
    }

    /// <summary>"GitHub" for GitHub and the servers its downloads come from; otherwise the host, or the local path.</summary>
    static string Describe(Uri url)
    {
        if (url.IsFile)
        {
            return url.LocalPath;
        }

        var host = url.Host;
        return host is "github.com" or "api.github.com" || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            ? "GitHub"
            : host;
    }
}
