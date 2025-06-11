using ROCKNIXImageBurner.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ROCKNIXImageBurner.Services
{
    /// <summary>
    /// Handles downloading image files and their SHA256 checksums, and verifies file integrity.
    /// Manages a local cache in the user's Downloads folder to avoid re-downloading files.
    /// </summary>
    public class ImageDownloader
    {
        private readonly HttpClient _httpClient;

        public ImageDownloader()
        {
            _httpClient = new HttpClient();
            // Set a user agent to be a good internet citizen.
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ROCKNIXImageBurner/1.0");
        }

        /// <summary>
        /// Downloads an image file, automatically verifying it against its SHA256 checksum if available.
        /// If a valid local file already exists, it will be used instead of re-downloading.
        /// If no checksum is provided, verification is skipped.
        /// </summary>
        /// <param name="imageInfo">The metadata for the image to download.</param>
        /// <param name="progress">An IProgress object to report download progress (0.0 to 100.0).</param>
        /// <param name="statusReporter">An Action to report status updates to the UI.</param>
        /// <returns>The local file path of the downloaded and verified image.</returns>
        public async Task<string> DownloadAndVerifyAsync(ImageInfo imageInfo, IProgress<double> progress, Action<string> statusReporter)
        {
            string targetFileName = GetTargetFileName(imageInfo);
            string downloadsFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloadsFolderPath);
            string targetFilePath = Path.Combine(downloadsFolderPath, targetFileName);

            statusReporter($"Preparing for image: {imageInfo.OriginalName}");

            try
            {
                string expectedSha256 = null;

                // 1. If a checksum URL is provided, fetch the expected hash first.
                if (!string.IsNullOrEmpty(imageInfo.Sha256Url))
                {
                    statusReporter("Fetching SHA256 checksum...");
                    string sha256FileContent = await _httpClient.GetStringAsync(imageInfo.Sha256Url);
                    expectedSha256 = sha256FileContent.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();

                    if (string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        throw new Exception("Could not parse SHA256 hash from the checksum file.");
                    }
                    statusReporter($"Expected SHA256: {expectedSha256}");
                }
                else
                {
                    statusReporter("No SHA256 checksum provided. Verification will be skipped.");
                }

                // 2. Check if a local file already exists and if it's valid.
                if (File.Exists(targetFilePath))
                {
                    // If we don't have a hash to check against, the existing file is considered good enough.
                    if (expectedSha256 == null)
                    {
                        statusReporter($"Using existing (unverified) file: {targetFileName}");
                        progress.Report(100.0);
                        return targetFilePath;
                    }

                    // If we do have a hash, we must verify the existing file.
                    statusReporter("Existing file found. Verifying checksum...");
                    string existingFileSha256 = await ComputeSha256Async(targetFilePath);
                    statusReporter($"Existing file SHA256: {existingFileSha256}");

                    if (existingFileSha256 == expectedSha256)
                    {
                        statusReporter($"Using existing verified file: {targetFileName}");
                        progress.Report(100.0);
                        return targetFilePath;
                    }
                    else
                    {
                        // If checksums don't match, the local file is corrupt or outdated. Delete it.
                        statusReporter("Checksum mismatch for existing file. Deleting and re-downloading.");
                        File.Delete(targetFilePath);
                    }
                }

                // 3. If we're here, we need to download the file.
                statusReporter($"Downloading {imageInfo.OriginalName} image...");
                await DownloadFileAsync(imageInfo.Url, targetFilePath, progress);
                statusReporter("Download complete.");

                // 4. If verification is required, verify the newly downloaded file.
                if (expectedSha256 != null)
                {
                    statusReporter("Verifying checksum of new file...");
                    string actualSha256 = await ComputeSha256Async(targetFilePath);
                    statusReporter($"Actual SHA256: {actualSha256}");

                    if (actualSha256 != expectedSha256)
                    {
                        // If the new file is corrupt, delete it and throw an error.
                        File.Delete(targetFilePath);
                        throw new Exception($"Checksum mismatch after download. Expected: {expectedSha256}, Actual: {actualSha256}. The corrupted file has been deleted.");
                    }
                    statusReporter("Checksum verified successfully.");
                }

                return targetFilePath;
            }
            catch (Exception ex)
            {
                statusReporter($"Error during download/verification: {ex.Message}");
                // Rethrow to be caught by the main window's logic.
                throw;
            }
        }

        /// <summary>
        /// Determines a safe and valid local filename for the image.
        /// </summary>
        private string GetTargetFileName(ImageInfo imageInfo)
        {
            string fileName;
            try
            {
                // The best source is the filename from the URL itself.
                fileName = Path.GetFileName(new Uri(imageInfo.Url).AbsolutePath);
            }
            catch (UriFormatException)
            {
                // If the URL is malformed, fall back to a sanitized version of the image name.
                fileName = SanitizeFilename(imageInfo.OriginalName) + ".img.gz";
            }

            // Ensure the filename is not empty and has a reasonable extension.
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = SanitizeFilename(imageInfo.OriginalName) + ".img.gz";
            }

            return fileName;
        }

        /// <summary>
        /// Downloads a file from a URL to a specified path, reporting progress along the way.
        /// </summary>
        private async Task DownloadFileAsync(string url, string outputPath, IProgress<double> progress)
        {
            using (HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                long totalBytesRead = 0;

                // Use a buffer to read from the network stream and write to the file stream.
                using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    byte[] buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;

                        // Report progress if the total size is known.
                        if (totalBytes.HasValue)
                        {
                            progress.Report((double)totalBytesRead / totalBytes.Value * 100.0);
                        }
                    }
                }
            }
            progress.Report(100.0); // Ensure final progress is 100%.
        }

        /// <summary>
        /// Computes the SHA256 checksum of a file asynchronously.
        /// </summary>
        /// <returns>The lowercase hexadecimal string of the SHA256 hash.</returns>
        private Task<string> ComputeSha256Async(string filePath)
        {
            return Task.Run(() =>
            {
                using (var sha256 = SHA256.Create())
                using (FileStream fileStream = File.OpenRead(filePath))
                {
                    byte[] hash = sha256.ComputeHash(fileStream);

                    // Convert the byte array hash to a lowercase hex string.
                    var sb = new StringBuilder();
                    foreach (byte b in hash)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    return sb.ToString();
                }
            });
        }

        /// <summary>
        /// Replaces characters that are invalid in filenames with underscores.
        /// </summary>
        private string SanitizeFilename(string name)
        {
            return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        }
    }
}