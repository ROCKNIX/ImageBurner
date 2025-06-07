namespace ROCKNIXImageBurner.Models
{
    /// <summary>
    /// Represents all the metadata for a single downloadable device image.
    /// </summary>
    public class ImageInfo
    {
        /// <summary>
        /// The original, full name of the image as specified in the source.
        /// e.g., "Radxa ROCK 5B"
        /// </summary>
        public string OriginalName { get; set; } = string.Empty;

        /// <summary>
        /// The manufacturer of the device. Parsed from the OriginalName.
        /// e.g., "Radxa"
        /// </summary>
        public string Manufacturer { get; set; } = string.Empty;

        /// <summary>
        /// The specific device model. Parsed from the OriginalName.
        /// e.g., "ROCK 5B"
        /// </summary>
        public string Device { get; set; } = string.Empty;

        /// <summary>
        /// The release branch for the image.
        /// e.g., "Stable" or "Nightly"
        /// </summary>
        public string Branch { get; set; } = string.Empty;

        /// <summary>
        /// The direct download URL for the compressed image file (.img.gz).
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// The URL for the file containing the SHA256 checksum for the image file.
        /// </summary>
        public string Sha256Url { get; set; } = string.Empty;

        /// <summary>
        /// A string indicating the type of post-installation script or action required.
        /// e.g., "dtb.img", "extlinux", "grubenv"
        /// </summary>
        public string PostInstall { get; set; } = string.Empty;

        /// <summary>
        /// The name of the Device Tree Blob (DTB) file associated with this image, without the extension.
        /// e.g., "rk3588-rock-5b"
        /// </summary>
        public string Dtb { get; set; } = string.Empty;

        /// <summary>
        /// A combined display name, primarily for potential UI use or debugging.
        /// </summary>
        public string DisplayName => $"{Manufacturer} {Device}";

        /// <summary>
        /// Returns a string representation of the image, useful for debugging.
        /// </summary>
        public override string ToString() => $"{Branch} - {Manufacturer} - {Device}";
    }
}