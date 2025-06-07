using ROCKNIXImageBurner.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management; // Requires System.Management assembly reference

namespace ROCKNIXImageBurner.Services
{
    /// <summary>
    /// Detects removable physical drives connected to the system using Windows Management Instrumentation (WMI).
    /// </summary>
    public class DriveDetector
    {
        #region Public Methods

        /// <summary>
        /// Scans the system for physical drives that are identified as removable and safe to write to.
        /// </summary>
        /// <returns>A list of DriveData objects representing suitable removable drives.</returns>
        public List<DriveData> GetRemovableDrives()
        {
            var drives = new List<DriveData>();
            Debug.WriteLine("DriveDetector: Starting GetRemovableDrives...");

            try
            {
                // Query for all physical disk drives in the system.
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
                {
                    ManagementObjectCollection diskDrives = searcher.Get();
                    Debug.WriteLine($"DriveDetector: Found {diskDrives.Count} Win32_DiskDrive instances.");

                    foreach (ManagementObject diskDriveObject in diskDrives.Cast<ManagementObject>())
                    {
                        string currentDeviceID = "Unknown";
                        try
                        {
                            // Standardize the DeviceID to the format expected by CreateFile (e.g., \\.\PHYSICALDRIVE1)
                            string rawDeviceID = diskDriveObject["DeviceID"]?.ToString();
                            if (string.IsNullOrEmpty(rawDeviceID))
                            {
                                Debug.WriteLine("DriveDetector: Skipping a Win32_DiskDrive with a null or empty DeviceID.");
                                continue;
                            }
                            currentDeviceID = rawDeviceID.StartsWith(".\\", StringComparison.Ordinal) ? "\\" + rawDeviceID : rawDeviceID;

                            // Gather properties from WMI.
                            string model = diskDriveObject["Model"]?.ToString() ?? "N/A_Model";
                            string pnpDeviceID = diskDriveObject["PNPDeviceID"]?.ToString() ?? string.Empty;
                            string mediaType = diskDriveObject["MediaType"]?.ToString() ?? "N/A_MediaType";
                            string interfaceType = diskDriveObject["InterfaceType"]?.ToString()?.ToUpperInvariant() ?? "N/A_InterfaceType";
                            ulong.TryParse(diskDriveObject["Size"]?.ToString(), out ulong size);

                            // Determine if the drive is removable based on its properties.
                            // A drive is considered removable if it's connected via USB, its media type is "removable",
                            // or its Plug and Play ID indicates it's a USB storage device.
                            bool isRemovable = interfaceType == "USB"
                                            || mediaType.ToLower().Contains("removable")
                                            || pnpDeviceID.ToUpper().StartsWith("USBSTOR");

                            // Crucially, we must also check if the drive is the system drive to prevent accidental erasure.
                            bool isSystemDrive = isRemovable && IsSystemDrive(currentDeviceID);

                            if (isRemovable && !isSystemDrive && size > 0)
                            {
                                Debug.WriteLine($"DriveDetector: Adding drive {currentDeviceID}. Model: {model}, Size: {FormatBytes(size)}");
                                drives.Add(new DriveData
                                {
                                    DeviceID = currentDeviceID,
                                    Model = model,
                                    Size = FormatBytes(size),
                                    RawSize = size,
                                    PNPDeviceID = pnpDeviceID
                                });
                            }
                            else
                            {
                                Debug.WriteLine($"DriveDetector: Skipping drive {currentDeviceID}. IsRemovable: {isRemovable}, IsSystemDrive: {isSystemDrive}, Size: {size}");
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log and rethrow exceptions that occur while processing a single drive.
                            Debug.WriteLine($"DriveDetector: Generic Exception while processing {currentDeviceID}. Message: {ex.Message}");
                            throw;
                        }
                    }
                }
            }
            catch (ManagementException ex)
            {
                // Handle errors from WMI itself.
                string errorMessage = $"Error accessing system drive information (WMI): {ex.Message} (Code: {ex.ErrorCode}). This may be a permissions issue.";
                Debug.WriteLine($"DriveDetector: WMI ManagementException in GetRemovableDrives. {errorMessage}");
                throw new Exception(errorMessage, ex);
            }
            catch (Exception ex)
            {
                // Handle any other unexpected errors.
                string errorMessage = $"An unexpected error occurred while detecting drives: {ex.Message}.";
                Debug.WriteLine($"DriveDetector: Generic Exception in GetRemovableDrives. {errorMessage}");
                throw new Exception(errorMessage, ex);
            }

            Debug.WriteLine($"DriveDetector: GetRemovableDrives completed. Found {drives.Count} suitable removable drives.");
            return drives;
        }

        /// <summary>
        /// Finds all volume paths (e.g., "C:\", "D:\") associated with a given physical drive.
        /// This is essential for locking and dismounting volumes before writing to the raw disk.
        /// </summary>
        /// <param name="physicalDriveDeviceID">The DeviceID of the physical drive (e.g., \\.\PHYSICALDRIVE1).</param>
        /// <returns>A list of volume paths.</returns>
        public List<string> GetVolumePathsForPhysicalDrive(string physicalDriveDeviceID)
        {
            var volumePaths = new List<string>();
            Debug.WriteLine($"DriveDetector: Finding volume paths for physical drive {physicalDriveDeviceID}.");

            try
            {
                // This method works by iterating through all logical disks and, for each one,
                // tracing back through its partitions to find the parent physical disk.
                string logicalDiskQuery = "SELECT DeviceID FROM Win32_LogicalDisk";
                using (var logicalDiskSearcher = new ManagementObjectSearcher(logicalDiskQuery))
                {
                    foreach (ManagementObject logicalDisk in logicalDiskSearcher.Get().Cast<ManagementObject>())
                    {
                        string logicalDiskDeviceID = logicalDisk["DeviceID"]?.ToString();
                        if (string.IsNullOrEmpty(logicalDiskDeviceID)) continue;

                        // Query for partitions associated with the current logical disk.
                        string partitionQuery = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{logicalDiskDeviceID}'}} WHERE AssocClass = Win32_LogicalDiskToPartition";
                        using (var partitionSearcher = new ManagementObjectSearcher(partitionQuery))
                        {
                            foreach (ManagementObject partition in partitionSearcher.Get().Cast<ManagementObject>())
                            {
                                // Query for the physical disk associated with the current partition.
                                string driveQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition";
                                using (var driveSearcher = new ManagementObjectSearcher(driveQuery))
                                {
                                    foreach (ManagementObject diskDrive in driveSearcher.Get().Cast<ManagementObject>())
                                    {
                                        string currentDiskDriveDeviceID = diskDrive["DeviceID"]?.ToString();
                                        if (string.IsNullOrEmpty(currentDiskDriveDeviceID)) continue;

                                        // If this physical disk matches our target, we've found a volume.
                                        if (string.Equals(currentDiskDriveDeviceID, physicalDriveDeviceID, StringComparison.OrdinalIgnoreCase))
                                        {
                                            string fullVolumePath = logicalDiskDeviceID + Path.DirectorySeparatorChar;
                                            if (!volumePaths.Contains(fullVolumePath))
                                            {
                                                volumePaths.Add(fullVolumePath);
                                                Debug.WriteLine($"DriveDetector: Found volume path: {fullVolumePath} on target drive {physicalDriveDeviceID}.");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DriveDetector: Error getting volume paths for {physicalDriveDeviceID}: {ex.Message}");
            }

            Debug.WriteLine($"DriveDetector: Found {volumePaths.Count} volume paths for {physicalDriveDeviceID}.");
            return volumePaths;
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Checks if a given physical drive contains the operating system partition.
        /// </summary>
        /// <param name="physicalDriveDeviceID">The device ID of the drive to check.</param>
        /// <returns>True if the drive is the system drive, false otherwise.</returns>
        private bool IsSystemDrive(string physicalDriveDeviceID)
        {
            try
            {
                string systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
                if (string.IsNullOrEmpty(systemDriveLetter)) return false;

                // WQL queries require backslashes to be escaped.
                string escapedDeviceID = physicalDriveDeviceID.Replace("\\", "\\\\");
                string partitionQuery = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escapedDeviceID}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition";

                using (var partitionSearcher = new ManagementObjectSearcher(partitionQuery))
                {
                    foreach (ManagementObject partition in partitionSearcher.Get().Cast<ManagementObject>())
                    {
                        string logicalDiskQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition";
                        using (var logicalDiskSearcher = new ManagementObjectSearcher(logicalDiskQuery))
                        {
                            foreach (ManagementObject logicalDisk in logicalDiskSearcher.Get().Cast<ManagementObject>())
                            {
                                string logicalDeviceID = logicalDisk["DeviceID"]?.ToString();
                                if (string.Equals(logicalDeviceID, systemDriveLetter, StringComparison.OrdinalIgnoreCase))
                                {
                                    Debug.WriteLine($"DriveDetector: Match! {physicalDriveDeviceID} is a system drive because it contains logical disk {logicalDeviceID}.");
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (ManagementException ex)
            {
                // This can happen if a drive has no partitions. We assume it's not a system drive in this case.
                Debug.WriteLine($"DriveDetector: WMI exception while checking if {physicalDriveDeviceID} is a system drive. Assuming it's not. Error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Formats a size in bytes into a human-readable string (KB, MB, GB, TB).
        /// </summary>
        /// <param name="bytes">The number of bytes.</param>
        /// <returns>A formatted string.</returns>
        private static string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return "0 B";

            const int scale = 1024;
            string[] orders = { "B", "KB", "MB", "GB", "TB" };
            int orderIndex = (int)(Math.Log(bytes) / Math.Log(scale));
            double scaledValue = bytes / Math.Pow(scale, orderIndex);

            return $"{scaledValue:0.##} {orders[orderIndex]}";
        }

        #endregion
    }
}