using ROCKNIXImageBurner.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ROCKNIXImageBurner.Services
{
    /// <summary>
    /// Fetches and parses the list of available images from a remote XML source.
    /// </summary>
    public class ImageFetcher
    {
        // The URL of the XML file that lists all available images.
        private const string XmlUrl = "https://releases.rocknix.org/imageburner";
        private readonly HttpClient _httpClient;

        public ImageFetcher()
        {
            _httpClient = new HttpClient();
            // Set a user agent to identify this application.
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ROCKNIXImageBurner/1.0");
        }

        /// <summary>
        /// Fetches the XML from the source URL and parses it into a list of ImageInfo objects.
        /// </summary>
        /// <returns>A list of all valid ImageInfo objects found in the XML.</returns>
        /// <exception cref="Exception">Throws an exception if the network request fails or the XML is malformed.</exception>
        public async Task<List<ImageInfo>> FetchImagesAsync()
        {
            var allImages = new List<ImageInfo>();
            try
            {
                string xmlContent = await _httpClient.GetStringAsync(XmlUrl);
                XDocument doc = XDocument.Parse(xmlContent);

                if (doc.Root == null)
                {
                    return allImages;
                }

                // The XML is expected to have root-level elements for each branch, e.g., <stable>, <nightly>.
                foreach (XElement branchElement in doc.Root.Elements())
                {
                    string branchName = branchElement.Name.LocalName; // "stable" or "nightly"

                    // Within each branch, there are <image> elements.
                    var imagesFromBranch = branchElement.Elements("image")
                        .Select(imgElement =>
                        {
                            string originalName = imgElement.Element("name")?.Value ?? "Unknown Image";
                            ParseName(originalName, out string manufacturer, out string device);

                            // Map the XML elements to the ImageInfo model.
                            return new ImageInfo
                            {
                                OriginalName = originalName,
                                Manufacturer = manufacturer,
                                Device = device,
                                // Capitalize the branch name for display purposes.
                                Branch = char.ToUpper(branchName[0]) + branchName.Substring(1),
                                Url = imgElement.Element("url")?.Value ?? string.Empty,
                                Sha256Url = imgElement.Element("sha256")?.Value ?? string.Empty,
                                PostInstall = imgElement.Element("post_install")?.Value ?? string.Empty,
                                Dtb = imgElement.Element("dtb")?.Value ?? string.Empty
                            };
                        })
                        // Only include images that have a download URL. The checksum URL is optional.
                        .Where(img => !string.IsNullOrEmpty(img.Url))
                        .ToList();

                    allImages.AddRange(imagesFromBranch);
                }

                return allImages;
            }
            catch (HttpRequestException ex)
            {
                // Handle network-related errors.
                throw new Exception($"Failed to fetch the image list from {XmlUrl}. Please check your internet connection.", ex);
            }
            catch (System.Xml.XmlException ex)
            {
                // Handle errors from malformed XML content.
                throw new Exception("Failed to parse the image list XML. The source file may be corrupt.", ex);
            }
            catch (Exception ex)
            {
                // Handle any other unexpected errors.
                throw new Exception($"An unexpected error occurred while fetching the image list: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// A simple helper method to parse a full device name into a manufacturer and a device model.
        /// It assumes the first word is the manufacturer.
        /// </summary>
        /// <param name="fullName">The full name to parse (e.g., "Radxa ROCK 5B").</param>
        /// <param name="manufacturer">The output manufacturer (e.g., "Radxa").</param>
        /// <param name="device">The output device model (e.g., "ROCK 5B").</param>
        private void ParseName(string fullName, out string manufacturer, out string device)
        {
            var parts = fullName.Split(new[] { ' ' }, 2);
            if (parts.Length > 1)
            {
                manufacturer = parts[0];
                device = parts[1];
            }
            else
            {
                // Fallback for names that don't fit the pattern.
                manufacturer = "Unknown";
                device = fullName;
            }
        }
    }
}