namespace ROCKNIXImageBurner.Models
{
    /// <summary>
    /// Represents all the necessary information about a physical drive
    /// that the application can write to.
    /// </summary>
    public class DriveData
    {
        /// <summary>
        /// The system identifier for the physical drive.
        /// e.g., "\\.\PHYSICALDRIVE1"
        /// </summary>
        public string DeviceID { get; set; } = string.Empty;

        /// <summary>
        /// The model name of the drive as reported by the system.
        /// e.g., "Generic Mass Storage"
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// A user-friendly string representing the total size of the drive.
        /// e.g., "14.9 GB"
        /// </summary>
        public string Size { get; set; } = string.Empty;

        /// <summary>
        /// The total size of the drive in bytes.
        /// </summary>
        public ulong RawSize { get; set; }

        /// <summary>
        /// The Plug and Play Device ID, used to help reliably identify removable drives.
        /// e.g., "USBSTOR\DISK&VEN_GENERIC&PROD_MASS_STORAGE&REV_1.00\..."
        /// </summary>
        public string PNPDeviceID { get; set; } = string.Empty;

        /// <summary>
        /// Gets the formatted string for display in UI elements like ComboBoxes.
        /// Prioritizes a clear and descriptive representation of the drive.
        /// </summary>
        public string DisplayString
        {
            get
            {
                // Some drives report generic or unhelpful model names.
                // In these cases, we fall back to a simpler display format for clarity.
                bool isModelGeneric = string.IsNullOrEmpty(Model)
                                   || Model.Trim() == "NVMe Device"
                                   || Model.Trim() == "N/A_Model";

                return isModelGeneric
                    ? $"{DeviceID} - {Size}"
                    : $"{Model} ({DeviceID}) - {Size}";
            }
        }

        /// <summary>
        /// Returns the display string for this drive.
        /// Useful for debugging and logging.
        /// </summary>
        public override string ToString() => DisplayString;
    }
}