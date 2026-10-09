using System;
using System.Net;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    /// <summary>Bounded Steam Community reads. Failures never delete saved cookies.</summary>
    public sealed class SteamHttpClient : IBoundedCommunityClient, IDisposable
    {
        public const int DefaultResponseLimit = 8 * 1024 * 1024;
        public const int MaximumResponseLimit = 32 * 1024 * 1024;
        private readonly HttpClient client;
        private readonly TimeSpan timeout;
        private readonly int maxRetries;

        public SteamHttpClient(CookieContainer cookies, TimeSpan? timeout = null, int maxRetries = 2)
            : this(new HttpClientHandler
            {
                CookieContainer = cookies ?? throw new ArgumentNullException(nameof(cookies)),
                UseCookies = true,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }, timeout, maxRetries)
        { }

        // An explicit transport supports tests without a live Steam account.
        public SteamHttpClient(HttpMessageHandler handler, TimeSpan? timeout = null, int maxRetries = 2)
        {
            this.timeout = timeout ?? TimeSpan.FromSeconds(15);
            if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (maxRetries < 0 || maxRetries > 3)
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            this.maxRetries = maxRetries;
            client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), true);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = DefaultResponseLimit;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IdleMasterExtended/1.12");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        }

        public Task<SteamReadResult<string>> GetAsync(string url, CancellationToken cancellationToken)
        {
            return GetAsync(url, DefaultResponseLimit, cancellationToken);
        }

        public async Task<SteamReadResult<string>> GetAsync(string url, int maximumResponseBytes, CancellationToken cancellationToken)
        {
            if (maximumResponseBytes < 1 || maximumResponseBytes > MaximumResponseLimit)
                throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !IsCommunityUri(uri))
                return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "The Steam Community address is invalid.");

            SteamReadResult<string> result = null;
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    attemptCancellation.CancelAfter(timeout);
                    try
                    {
                        result = await ReadAsync(uri, maximumResponseBytes, attemptCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result = SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam took too long to respond. Try again shortly.");
                    }
                    catch (HttpRequestException)
                    {
                        result = SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam could not be reached. Check your connection and try again.");
                    }
                    catch (IOException)
                    {
                        result = SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam's response was interrupted. Try again shortly.");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Status != SteamReadStatus.TransientFailure || attempt == maxRetries)
                    return result;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        private async Task<SteamReadResult<string>> ReadAsync(Uri uri, int maximumResponseBytes, CancellationToken cancellationToken)
        {
            var current = uri;
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                using (var cancelResponse = cancellationToken.Register(() => response.Dispose()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = (int)response.StatusCode;
                    if (status >= 300 && status <= 399)
                    {
                        var location = response.Headers.Location;
                        if (location == null)
                            return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam returned an incomplete redirect.");
                        Uri next;
                        if (!Uri.TryCreate(current, location, out next))
                            return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam returned an invalid redirect.");
                        if (IsLoginUri(next))
                            return SteamReadResult<string>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam again to continue.");
                        if (!IsCommunityUri(next))
                            return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam redirected outside the Community site.");
                        current = next;
                        continue;
                    }
                    if (status == 401)
                        return SteamReadResult<string>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam again to continue.");
                    if (status == 429 || status == 408 || status >= 500 || status == 403)
                        return SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure,
                            status == 429 ? "Steam is limiting requests. Wait a little and try again." : "Steam is temporarily unavailable. Try again shortly.");
                    if (!response.IsSuccessStatusCode)
                        return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam could not provide the requested page.");

                    return await ReadContentAsync(response.Content, maximumResponseBytes, cancellationToken).ConfigureAwait(false);
                }
            }
            return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam redirected too many times.");
        }

        internal static async Task<SteamReadResult<string>> ReadContentAsync(HttpContent content, int maximumResponseBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content == null)
                return SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam returned an empty response. Try again shortly.");
            if (content.Headers.ContentLength > maximumResponseBytes)
                return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam's response was too large to read safely. Your previous game list has been kept.");
            try
            {
                using (var source = await content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var cancelRead = cancellationToken.Register(() => source.Dispose()))
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[16 * 1024];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await source.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        if (buffer.Length + read > maximumResponseBytes)
                            return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam's response was too large to read safely. Your previous game list has been kept.");
                        buffer.Write(chunk, 0, read);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var encoding = Encoding.UTF8;
                    var charset = content.Headers.ContentType?.CharSet;
                    if (!string.IsNullOrWhiteSpace(charset))
                    {
                        try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
                        catch (ArgumentException)
                        {
                            return SteamReadResult<string>.Failed(SteamReadStatus.MalformedPage, "Steam returned an unsupported text format.");
                        }
                    }
                    buffer.Position = 0;
                    string text;
                    using (var reader = new StreamReader(buffer, encoding, true))
                        text = reader.ReadToEnd();
                    cancellationToken.ThrowIfCancellationRequested();
                    return string.IsNullOrWhiteSpace(text)
                        ? SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam returned an empty response. Try again shortly.")
                        : SteamReadResult<string>.Succeeded(text);
                }
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static bool IsCommunityUri(Uri uri)
        {
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
                && string.Equals(uri.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLoginUri(Uri uri)
        {
            return string.Equals(uri.Host, "login.steampowered.com", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(uri.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                    && (uri.AbsolutePath.Equals("/login", StringComparison.OrdinalIgnoreCase)
                        || uri.AbsolutePath.StartsWith("/login/", StringComparison.OrdinalIgnoreCase)));
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
