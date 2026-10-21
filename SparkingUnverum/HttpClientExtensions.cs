using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SparkingUnverum
{
    public static class HttpClientExtensions
    {
        public static long GetDirectorySize(this DirectoryInfo directoryInfo, bool recursive = true)
        {
            long size = 0;
            if (directoryInfo == null || !directoryInfo.Exists)
                return size;
            foreach (var fileInfo in directoryInfo.GetFiles())
                Interlocked.Add(ref size, fileInfo.Length);
            if (recursive)
                Parallel.ForEach(directoryInfo.GetDirectories(), subDirectory =>
                    Interlocked.Add(ref size, GetDirectorySize(subDirectory, recursive)));
            return size;
        }
        public static async Task DownloadAsync(this HttpClient client, string requestUri, Stream destination, string fileName, IProgress<DownloadProgress> progress = null, CancellationToken cancellationToken = default)
        {
            // Get the http headers first to examine the content length
            using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (progress == null || !contentLength.HasValue)
            {
                await download.CopyToAsync(destination, cancellationToken);
                return;
            }
            // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
            var relativeProgress = new Progress<long>(totalBytes => progress.Report(new DownloadProgress((float)totalBytes / contentLength.Value, totalBytes, contentLength.Value, fileName)));
            await download.CopyToAsync(destination, 81920, relativeProgress, cancellationToken);
            progress.Report(new DownloadProgress(1, contentLength.Value, contentLength.Value, fileName));
        }
    }
    public static class StreamExtensions
    {
        public static async Task CopyToAsync(this Stream source, Stream destination, int bufferSize, IProgress<long> progress = null, CancellationToken cancellationToken = default)
        {
            var buffer = new byte[bufferSize];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalBytesRead += bytesRead;
                progress?.Report(totalBytesRead);
            }
        }
    }
}
