using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;
using Microsoft.Web.LibraryManager.Utilities;

namespace Microsoft.Web.LibraryManager.Cache
{
    /// <summary>
    /// Service to manage basic operations on libraries cache
    /// </summary>
    public class CustomCacheService : CacheService
    {
        private const int DefaultCacheExpiresAfterMinutes = 10;
        private const int MaxConcurrentDownloads = 10;

        private readonly IWebRequestHandler _requestHandler;

        private static string CacheFolderValue;

        /// <summary>
        /// Instantiate the CustomCacheService
        /// </summary>
        /// <param name="requestHandler"></param>
        public CustomCacheService(IWebRequestHandler requestHandler) : base(requestHandler)
        {
            _requestHandler = requestHandler;
        }

        /// <summary>
        /// Downloads a resource from specified url to a destination file
        /// </summary>
        /// <param name="url">Url to download</param>
        /// <param name="fileName">Destination file path</param>
        /// <param name="attempts">Number of times to attempt the download</param>
        /// <param name="cancellationToken">Cancellation token</param>
        private async Task DownloadToFileAsync(string url, string fileName, int attempts, CancellationToken cancellationToken)
        {
            if (attempts < 1)
            {
                throw new ArgumentException("Must attempt at least one time", nameof(attempts));
            }

            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    using (Stream libraryStream = await _requestHandler.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
                    {
                        await FileHelpers.SafeWriteToFileAsync(fileName, libraryStream, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                }
                catch (ResourceDownloadException)
                {
                    // rethrow last exception
                    if (i == attempts - 1)
                    {
                        throw;
                    }
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string> GetResourceAsync(string url, string localFile, int expirationMinutes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(localFile) || File.GetLastWriteTime(localFile) < DateTime.Now.AddMinutes(-expirationMinutes))
            {
                await DownloadToFileAsync(url, localFile, attempts: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await FileHelpers.ReadFileAsTextAsync(localFile, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Refreshes the cache for the given set of files if expired
        /// </summary>
        public override async Task RefreshCacheAsync(IEnumerable<CacheFileMetadata> librariesCacheMetadata, ILogger logger, CancellationToken cancellationToken)
        {
            await ParallelUtility.ForEachAsync(DownloadFileIfNecessaryAsync, MaxConcurrentDownloads, librariesCacheMetadata, cancellationToken).ConfigureAwait(false);

            async Task DownloadFileIfNecessaryAsync(CacheFileMetadata metadata)
            {
                if (!File.Exists(metadata.DestinationPath))
                {
                    logger.Log(string.Format(Resources.Text.DownloadingFile, metadata.Source), LogLevel.Operation);
                    await DownloadToFileAsync(metadata.Source, metadata.DestinationPath, attempts: 5, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public override async Task<string> GetContentsFromUriWithCacheFallbackAsync(string url, string cacheFile, CancellationToken cancellationToken)
        {
            string contents;
            try
            {
                contents = await GetResourceAsync(url, cacheFile, DefaultCacheExpiresAfterMinutes, cancellationToken).ConfigureAwait(false);
            }
            catch (ResourceDownloadException)
            {
                // TODO: Log telemetry
                if (File.Exists(cacheFile))
                {
                    contents = await FileHelpers.ReadFileAsTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
            }

            return contents;
        }

        /// <inheritdoc />
        public override async Task<string> GetContentsFromCachedFileWithWebRequestFallbackAsync(string cacheFile, string url, CancellationToken cancellationToken)
        {
            string contents;
            if (File.Exists(cacheFile))
            {
                contents = await FileHelpers.ReadFileAsTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                contents = await GetResourceAsync(url, cacheFile, DefaultCacheExpiresAfterMinutes, cancellationToken).ConfigureAwait(false);
            }

            return contents;
        }
    }
}
