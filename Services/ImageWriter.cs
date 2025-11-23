using Microsoft.Win32.SafeHandles;
using ROCKNIXImageBurner.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ROCKNIXImageBurner.Services
{
    /// <summary>
    /// Handles the low-level, raw writing of a disk image to a physical drive.
    /// This class uses P/Invoke for direct disk access and requires administrator privileges.
    /// </summary>
    public class ImageWriter
    {
        private const int DecompressionBufferSize = 1 * 1024 * 1024; // 1 MB buffer for decompressing the GZip stream.
        private const uint WipeSizeBytes = 1 * 1024 * 1024;          // 1 MB area at the start of the disk to wipe.

        /// <summary>
        /// Writes a gzipped image file to a physical drive. This is a highly destructive operation.
        /// </summary>
        /// <param name="gzippedImagePath">Path to the source .img.gz file.</param>
        /// <param name="driveDeviceID">The device ID of the target drive (e.g., \\.\PHYSICALDRIVE1).</param>
        /// <param name="volumePathsToManage">A list of volumes on the drive (e.g., "D:\") that need to be locked and dismounted.</param>
        /// <param name="progress">An IProgress object to report write progress (0.0 to 1.0).</param>
        /// <param name="selectedImageInfo">The metadata for the image being written, used for post-install actions.</param>
        public async Task WriteImageAsync(string gzippedImagePath, string driveDeviceID, List<string> volumePathsToManage, IProgress<double> progress, ImageInfo selectedImageInfo)
        {
            SafeFileHandle driveHandle = null;
            var volumeHandles = new List<SafeFileHandle>();

            try
            {
                // STAGE 1: Lock and Dismount Volumes
                // To gain exclusive access to the physical drive, we must first lock and dismount any
                // volumes (like D:, E:) that Windows has mounted from it.
                ManageVolumes(volumePathsToManage, volumeHandles, lockAndDismount: true);

                // STAGE 2: Open Physical Drive
                // We open a handle to the physical drive using CreateFile. The flags are critical:
                // - FILE_FLAG_NO_BUFFERING and FILE_FLAG_WRITE_THROUGH are used to bypass the OS cache
                //   and ensure writes go directly to the disk, which is essential for raw access and
                //   prevents stale data issues. This requires writes to be sector-aligned.
                driveHandle = NativeMethods.CreateFile(
                    driveDeviceID,
                    FileAccess.Write,
                    FileShare.None, // Exclusive access
                    IntPtr.Zero,
                    FileMode.Open,
                    NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_WRITE_THROUGH | NativeMethods.FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (driveHandle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open drive {driveDeviceID}.");
                }
                Debug.WriteLine($"ImageWriter: Successfully opened physical drive {driveDeviceID}.");

                // STAGE 3: Get Drive Geometry
                // We need the drive's sector size to perform sector-aligned writes.
                uint sectorSize = GetDriveSectorSize(driveHandle, driveDeviceID);

                // STAGE 4: Wipe Partition Table
                // We wipe the first megabyte of the disk to remove any old partition tables (MBR, GPT).
                // This ensures a clean state before writing the new image.
                WipeDiskStart(driveHandle, driveDeviceID, sectorSize);

                // STAGE 5: Write Image
                // Decompress the .gz file on the fly and write the raw .img data to the drive.
                await WriteDecompressedImageToDrive(gzippedImagePath, driveHandle, sectorSize, progress);

                progress.Report(100.0);
            }
            finally
            {
                // STAGE 6: Cleanup and Finalization
                // This block ensures all handles are closed and the system is notified of the changes.
                // It runs even if an error occurred during the write process.
                if (driveHandle != null && !driveHandle.IsInvalid)
                {
                    // Ensure all data is written to the physical disk before closing the handle.
                    NativeMethods.FlushFileBuffers(driveHandle);
                    driveHandle.Close();
                    Debug.WriteLine($"ImageWriter: Closed main physical drive handle for {driveDeviceID}.");
                }

                // Unlock the volumes we locked at the beginning.
                ManageVolumes(volumePathsToManage, volumeHandles, lockAndDismount: false);

                // Tell Windows to rescan the drive's partition table. This makes the new volumes appear in Explorer.
                UpdateDiskProperties(driveDeviceID);

                // STAGE 7: Post-Install Actions
                // Some images require modifications after being written, like setting the correct
                // device tree blob (DTB) for the specific hardware.
                await PerformPostInstallActions(selectedImageInfo);
            }
        }

        #region Private Worker Methods

        /// <summary>
        /// Opens handles to volumes and either locks/dismounts them or unlocks them.
        /// </summary>
        private void ManageVolumes(List<string> volumePaths, List<SafeFileHandle> volumeHandles, bool lockAndDismount)
        {
            if (lockAndDismount)
            {
                foreach (var volPath in volumePaths)
                {
                    string volumeDevicePath = @"\\.\" + volPath.TrimEnd(Path.DirectorySeparatorChar);
                    Debug.WriteLine($"ImageWriter: Attempting to open volume {volumeDevicePath} to lock/dismount.");

                    SafeFileHandle volHandle = NativeMethods.CreateFile(volumeDevicePath, FileAccess.ReadWrite, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);

                    if (volHandle != null && !volHandle.IsInvalid)
                    {
                        volumeHandles.Add(volHandle);
                        // Lock the volume to prevent other processes from using it.
                        if (!NativeMethods.DeviceIoControl(volHandle, NativeMethods.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                        {
                            Debug.WriteLine($"ImageWriter: Failed to lock volume {volumeDevicePath}. Error: {Marshal.GetLastWin32Error()}.");
                        }
                        // Dismount the volume to detach the file system.
                        if (!NativeMethods.DeviceIoControl(volHandle, NativeMethods.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                        {
                            Debug.WriteLine($"ImageWriter: Failed to dismount volume {volumeDevicePath}. Error: {Marshal.GetLastWin32Error()}.");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"ImageWriter: Could not open volume {volumeDevicePath}. Error: {Marshal.GetLastWin32Error()}");
                    }
                }
            }
            else // Unlock
            {
                foreach (var volHandle in volumeHandles)
                {
                    if (volHandle != null && !volHandle.IsInvalid)
                    {
                        // The order is not critical here, but unlocking is the important part.
                        NativeMethods.DeviceIoControl(volHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                        volHandle.Close();
                    }
                }
                Debug.WriteLine("ImageWriter: Finished cleanup of volume handles.");
            }
        }

        /// <summary>
        /// Queries the drive for its physical sector size.
        /// </summary>
        private uint GetDriveSectorSize(SafeFileHandle driveHandle, string driveDeviceID)
        {
            uint sectorSize = 512; // Default to a safe, common value.
            var diskGeometry = new NativeMethods.DISK_GEOMETRY();
            uint diskGeometrySize = (uint)Marshal.SizeOf(diskGeometry);
            IntPtr diskGeometryBuffer = Marshal.AllocHGlobal((int)diskGeometrySize);

            try
            {
                if (NativeMethods.DeviceIoControl(driveHandle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY, IntPtr.Zero, 0, diskGeometryBuffer, diskGeometrySize, out _, IntPtr.Zero))
                {
                    diskGeometry = (NativeMethods.DISK_GEOMETRY)Marshal.PtrToStructure(diskGeometryBuffer, typeof(NativeMethods.DISK_GEOMETRY));
                    if (diskGeometry.BytesPerSector > 0)
                    {
                        sectorSize = diskGeometry.BytesPerSector;
                        Debug.WriteLine($"ImageWriter: Obtained sector size: {sectorSize} bytes for drive {driveDeviceID}");
                    }
                }
                else
                {
                    Debug.WriteLine($"ImageWriter: Warning: Failed to get drive geometry (Error: {Marshal.GetLastWin32Error()}). Defaulting sector size to {sectorSize}.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(diskGeometryBuffer);
            }
            return sectorSize;
        }

        /// <summary>
        /// Writes zeroes to the beginning of the disk to clear any existing partition data.
        /// </summary>
        private void WipeDiskStart(SafeFileHandle driveHandle, string driveDeviceID, uint sectorSize)
        {
            Debug.WriteLine($"ImageWriter: Wiping first {WipeSizeBytes} bytes of {driveDeviceID}...");
            uint alignedWipeSize = ((WipeSizeBytes + sectorSize - 1) / sectorSize) * sectorSize;
            byte[] wipeBuffer = new byte[alignedWipeSize]; // Zero-initialized by default.

            if (!NativeMethods.WriteFile(driveHandle, wipeBuffer, alignedWipeSize, out uint bytesWritten, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to wipe partition table on {driveDeviceID}.");
            }
            if (bytesWritten != alignedWipeSize)
            {
                throw new IOException($"Incomplete wipe of partition table on {driveDeviceID}.");
            }

            // Set the file pointer back to the beginning of the disk to prepare for writing the image.
            if (!NativeMethods.SetFilePointerEx(driveHandle, 0, IntPtr.Zero, NativeMethods.FILE_BEGIN))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to reset file pointer after wipe.");
            }
        }

        /// <summary>
        /// Reads a GZip stream, decompresses it, and writes the data to the drive handle.
        /// </summary>
        private async Task WriteDecompressedImageToDrive(string gzippedImagePath, SafeFileHandle driveHandle, uint sectorSize, IProgress<double> progress)
        {
            Debug.WriteLine($"ImageWriter: Starting image write from '{gzippedImagePath}'.");
            using (FileStream gzippedFileStream = new FileStream(gzippedImagePath, FileMode.Open, FileAccess.Read))
            using (GZipStream decompressionStream = new GZipStream(gzippedFileStream, CompressionMode.Decompress))
            {
                // Attempt to get the original uncompressed size from the GZip footer for better progress reporting.
                long decompressedLength = GetDecompressedLength(gzippedFileStream);

                // The write buffer must be a multiple of the sector size.
                int writeBufferSize = (int)(((uint)DecompressionBufferSize + sectorSize - 1) / sectorSize * sectorSize);
                byte[] writeBuffer = new byte[writeBufferSize];
                int writeBufferOffset = 0;
                long totalBytesWritten = 0;

                byte[] readBuffer = new byte[DecompressionBufferSize];
                int bytesReadFromStream;

                while ((bytesReadFromStream = await decompressionStream.ReadAsync(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    int readBufferOffset = 0;
                    while (readBufferOffset < bytesReadFromStream)
                    {
                        int bytesToCopy = Math.Min(bytesReadFromStream - readBufferOffset, writeBuffer.Length - writeBufferOffset);
                        Buffer.BlockCopy(readBuffer, readBufferOffset, writeBuffer, writeBufferOffset, bytesToCopy);
                        readBufferOffset += bytesToCopy;
                        writeBufferOffset += bytesToCopy;

                        // When the write buffer is full, write its contents to the disk.
                        if (writeBufferOffset == writeBuffer.Length)
                        {
                            if (!NativeMethods.WriteFile(driveHandle, writeBuffer, (uint)writeBuffer.Length, out uint bytesWritten, IntPtr.Zero))
                            {
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "Error during sector-aligned write to drive.");
                            }
                            totalBytesWritten += bytesWritten;
                            //Debug.WriteLine($"ImageWriter: Wrote a {bytesWritten}-byte chunk to disk. Total written: {totalBytesWritten}");
                            writeBufferOffset = 0;
                        }
                    }

                    // Report progress based on either decompressed size or compressed file position.
                    double currentProgress = decompressedLength > 0
                        ? (double)totalBytesWritten / decompressedLength * 100.0
                        : (double)gzippedFileStream.Position / gzippedFileStream.Length * 100.0;
                    progress.Report(currentProgress);
                }

                // Write any remaining data in the buffer.
                if (writeBufferOffset > 0)
                {
                    // Pad the final buffer with zeroes to make it sector-aligned.
                    uint finalBytesToWrite = ((uint)writeBufferOffset + sectorSize - 1) / sectorSize * sectorSize;
                    Array.Clear(writeBuffer, writeBufferOffset, (int)finalBytesToWrite - writeBufferOffset);

                    if (!NativeMethods.WriteFile(driveHandle, writeBuffer, finalBytesToWrite, out uint finalBytesWritten, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Error during final padded write to drive.");
                    }
                    totalBytesWritten += writeBufferOffset; // Only add the actual data size, not the padding.
                    Debug.WriteLine($"ImageWriter: Wrote final padded chunk of {finalBytesWritten} bytes. Original data size: {writeBufferOffset}.");
                }

                Debug.WriteLine($"ImageWriter: Finished image write. Total bytes written: {totalBytesWritten}");
            }
        }

        /// <summary>
        /// Reads the ISIZE field from the GZip footer to get the uncompressed file size.
        /// </summary>
        /// <returns>The decompressed size, or -1 if it cannot be determined.</returns>
        private long GetDecompressedLength(FileStream gzippedFileStream)
        {
            if (gzippedFileStream.CanSeek && gzippedFileStream.Length >= 8)
            {
                try
                {
                    byte[] isizeBuffer = new byte[4];
                    gzippedFileStream.Position = gzippedFileStream.Length - 4;
                    gzippedFileStream.Read(isizeBuffer, 0, 4);
                    gzippedFileStream.Position = 0; // Reset position
                    return BitConverter.ToUInt32(isizeBuffer, 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ImageWriter: Could not read GZip ISIZE footer: {ex.Message}");
                }
            }
            return -1;
        }

        /// <summary>
        /// Sends a control code to the OS to request a rescan of the disk.
        /// </summary>
        private void UpdateDiskProperties(string driveDeviceID)
        {
            SafeFileHandle updateHandle = null;
            try
            {
                // A new handle is needed for this operation.
                updateHandle = NativeMethods.CreateFile(driveDeviceID, (FileAccess)NativeMethods.GENERIC_WRITE, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
                if (updateHandle != null && !updateHandle.IsInvalid)
                {
                    if (!NativeMethods.DeviceIoControl(updateHandle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        Debug.WriteLine($"ImageWriter: Failed to update disk properties for {driveDeviceID}. Error: {Marshal.GetLastWin32Error()}.");
                    }
                    else
                    {
                        Debug.WriteLine($"ImageWriter: Successfully requested disk properties update for {driveDeviceID}.");
                    }
                }
            }
            finally
            {
                updateHandle?.Close();
            }
        }

        /// <summary>
        /// Performs post-write modifications to the new volumes based on the image metadata.
        /// </summary>
        private async Task PerformPostInstallActions(ImageInfo imageInfo)
        {
            if (imageInfo == null || string.IsNullOrEmpty(imageInfo.PostInstall) || string.IsNullOrEmpty(imageInfo.Dtb))
            {
                Debug.WriteLine("ImageWriter: No post-install action required.");
                return;
            }

            Debug.WriteLine($"ImageWriter: Post-install action '{imageInfo.PostInstall}' found for DTB '{imageInfo.Dtb}'.");

            // Wait for the "ROCKNIX" volume to be mounted by the OS.
            string rocknixDrive = await WaitForVolumeMountAsync("ROCKNIX");
            if (rocknixDrive == null)
            {
                Debug.WriteLine("ImageWriter: ROCKNIX volume did not mount. Skipping post-install actions.");
                return;
            }

            switch (imageInfo.PostInstall.ToLowerInvariant())
            {
                case "dtb.img":
                    // Action: Copy the correct DTB file to the root as 'dtb.img'.
                    string dtbSourcePath = Path.Combine(rocknixDrive, "device_trees", imageInfo.Dtb + ".dtb");
                    string dtbDestPath = Path.Combine(rocknixDrive, "dtb.img");
                    if (File.Exists(dtbSourcePath))
                    {
                        File.Copy(dtbSourcePath, dtbDestPath, true);
                        Debug.WriteLine($"ImageWriter: Copied {dtbSourcePath} to {dtbDestPath}.");
                    }
                    else
                    {
                        Debug.WriteLine($"ImageWriter: DTB source file not found at {dtbSourcePath}.");
                    }
                    break;

                case "extlinux":
                    // Action: Modify the 'extlinux.conf' file to point to the correct DTB.
                    string extlinuxConfPath = Path.Combine(rocknixDrive, "extlinux", "extlinux.conf");
                    if (File.Exists(extlinuxConfPath))
                    {
                        string[] lines = File.ReadAllLines(extlinuxConfPath);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Trim().StartsWith("FDT", StringComparison.OrdinalIgnoreCase))
                            {
                                // Preserve the original line's leading whitespace to maintain formatting.
                                string originalLine = lines[i];
                                int indentLength = originalLine.Length - originalLine.TrimStart().Length;
                                string leadingWhitespace = originalLine.Substring(0, indentLength);

                                lines[i] = $"{leadingWhitespace}FDT /device_trees/{imageInfo.Dtb}.dtb";
                                File.WriteAllLines(extlinuxConfPath, lines);
                                Debug.WriteLine($"ImageWriter: Modified {extlinuxConfPath} to use {imageInfo.Dtb}.dtb.");
                                break;
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"ImageWriter: extlinux.conf not found at {extlinuxConfPath}.");
                    }
                    break;

                case "grubenv":
                    // Action: Create or overwrite the 'grubenv' file to set the boot entry.
                    string grubEnvPath = Path.Combine(rocknixDrive, "EFI", "BOOT", "grubenv");
                    try
                    {
                        // The grubenv file must be exactly 1024 bytes.
                        const int grubEnvTotalSize = 1024;
                        byte[] grubEnvContent = new byte[grubEnvTotalSize];

                        // Define the exact header string, including newlines, as seen in a valid grubenv file.
                        const string grubEnvHeader = "# GRUB Environment Block\n# WARNING: Do not edit this file by tools other than grub-editenv!!!\n";
                        string entryLine = $"saved_entry={imageInfo.Dtb}\n";
                        string fullContentString = grubEnvHeader + entryLine;

                        // Convert the text content to bytes.
                        byte[] contentBytes = Encoding.UTF8.GetBytes(fullContentString);

                        // Fill the entire buffer with the '#' padding character first.
                        for (int i = 0; i < grubEnvTotalSize; i++)
                        {
                            grubEnvContent[i] = (byte)'#';
                        }

                        // Copy the header and entry over the padding at the beginning of the buffer.
                        // This ensures the final file structure is correct.
                        Buffer.BlockCopy(contentBytes, 0, grubEnvContent, 0, contentBytes.Length);

                        // Ensure the directory exists before writing the file.
                        Directory.CreateDirectory(Path.GetDirectoryName(grubEnvPath));
                        File.WriteAllBytes(grubEnvPath, grubEnvContent);
                        Debug.WriteLine($"ImageWriter: Wrote grubenv file at {grubEnvPath} with entry '{entryLine.Trim()}'.");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ImageWriter: Error modifying grubenv: {ex.Message}");
                    }
                    break;

                case "grubenv+linuxloader":
                    // Action: Combined grubenv and LinuxLoader.cfg modification.
                    // The DTB is expected to be in the format "grub_entry+dtb_name".
                    string[] dtbParts = imageInfo.Dtb.Split(new[] { '+' }, 2);
                    if (dtbParts.Length != 2)
                    {
                        Debug.WriteLine($"ImageWriter: Invalid DTB format for grubenv+linuxloader: '{imageInfo.Dtb}'. Expected 'grub_entry+dtb_name'.");
                        break;
                    }
                    string grubEntry = dtbParts[0];
                    string dtbName = dtbParts[1];

                    // Part 1: Perform grubenv action.
                    string grubEnvPath_combo = Path.Combine(rocknixDrive, "EFI", "BOOT", "grubenv");
                    try
                    {
                        const int grubEnvTotalSize = 1024;
                        byte[] grubEnvContent = new byte[grubEnvTotalSize];
                        const string grubEnvHeader = "# GRUB Environment Block\n# WARNING: Do not edit this file by tools other than grub-editenv!!!\n";
                        string entryLine = $"saved_entry={grubEntry}\n";
                        string fullContentString = grubEnvHeader + entryLine;
                        byte[] contentBytes = Encoding.UTF8.GetBytes(fullContentString);

                        for (int i = 0; i < grubEnvTotalSize; i++)
                        {
                            grubEnvContent[i] = (byte)'#';
                        }
                        Buffer.BlockCopy(contentBytes, 0, grubEnvContent, 0, contentBytes.Length);

                        Directory.CreateDirectory(Path.GetDirectoryName(grubEnvPath_combo));
                        File.WriteAllBytes(grubEnvPath_combo, grubEnvContent);
                        Debug.WriteLine($"ImageWriter: Wrote grubenv file at {grubEnvPath_combo} with entry '{entryLine.Trim()}'.");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ImageWriter: Error modifying grubenv for combo action: {ex.Message}");
                    }

                    // Part 2: Modify LinuxLoader.cfg.
                    string linuxLoaderCfgPath = Path.Combine(rocknixDrive, "LinuxLoader.cfg");
                    if (File.Exists(linuxLoaderCfgPath))
                    {
                        string[] lines = File.ReadAllLines(linuxLoaderCfgPath);
                        bool lineModified = false;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Trim().StartsWith("devicetree", StringComparison.OrdinalIgnoreCase))
                            {
                                lines[i] = $"devicetree = \"/boot/{dtbName}.dtb\"";
                                File.WriteAllLines(linuxLoaderCfgPath, lines);
                                Debug.WriteLine($"ImageWriter: Modified {linuxLoaderCfgPath} to use {dtbName}.dtb.");
                                lineModified = true;
                                break;
                            }
                        }
                        if (!lineModified)
                        {
                            Debug.WriteLine($"ImageWriter: 'devicetree' line not found in {linuxLoaderCfgPath}.");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"ImageWriter: LinuxLoader.cfg not found at {linuxLoaderCfgPath}.");
                    }
                    break;
                case "grubenv+dtb.img":
                    // Action: Combined grubenv and dtb.img modification.
                    // The DTB is expected to be in the format "grub_entry+dtb_name".
                    string[] dtbParts_combo = imageInfo.Dtb.Split(new[] { '+' }, 2);
                    if (dtbParts_combo.Length != 2)
                    {
                        Debug.WriteLine($"ImageWriter: Invalid DTB format for grubenv+dtb.img: '{imageInfo.Dtb}'. Expected 'grub_entry+dtb_name'.");
                        break;
                    }
                    string grubEntry_combo = dtbParts_combo[0];
                    string dtbName_combo = dtbParts_combo[1];

                    // Part 1: Perform grubenv action.
                    string grubEnvPath_new = Path.Combine(rocknixDrive, "boot", "grub", "grubenv");
                    try
                    {
                        const int grubEnvTotalSize = 1024;
                        byte[] grubEnvContent = new byte[grubEnvTotalSize];
                        const string grubEnvHeader = "# GRUB Environment Block\n# WARNING: Do not edit this file by tools other than grub-editenv!!!\n";
                        string entryLine = $"saved_entry={grubEntry_combo}\n";
                        string fullContentString = grubEnvHeader + entryLine;
                        byte[] contentBytes = Encoding.UTF8.GetBytes(fullContentString);

                        for (int i = 0; i < grubEnvTotalSize; i++)
                        {
                            grubEnvContent[i] = (byte)'#';
                        }
                        Buffer.BlockCopy(contentBytes, 0, grubEnvContent, 0, contentBytes.Length);

                        Directory.CreateDirectory(Path.GetDirectoryName(grubEnvPath_new));
                        File.WriteAllBytes(grubEnvPath_new, grubEnvContent);
                        Debug.WriteLine($"ImageWriter: Wrote grubenv file at {grubEnvPath_new} with entry '{entryLine.Trim()}'.");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ImageWriter: Error modifying grubenv for combo action: {ex.Message}");
                    }

                    // Part 2: Copy the correct DTB file to the root as 'dtb.img'.
                    string dtbSourcePath_combo = Path.Combine(rocknixDrive, "device_trees", dtbName_combo + ".dtb");
                    string dtbDestPath_combo = Path.Combine(rocknixDrive, "dtb.img");
                    if (File.Exists(dtbSourcePath_combo))
                    {
                        File.Copy(dtbSourcePath_combo, dtbDestPath_combo, true);
                        Debug.WriteLine($"ImageWriter: Copied {dtbSourcePath_combo} to {dtbDestPath_combo}.");
                    }
                    else
                    {
                        Debug.WriteLine($"ImageWriter: DTB source file not found at {dtbSourcePath_combo}.");
                    }
                    break;
            }
        }

        /// <summary>
        /// Waits asynchronously for a drive with a specific volume label to be mounted by the OS.
        /// </summary>
        /// <param name="volumeLabel">The volume label to search for (e.g., "ROCKNIX").</param>
        /// <returns>The root path of the drive (e.g., "D:\") or null if not found after several retries.</returns>
        private static async Task<string> WaitForVolumeMountAsync(string volumeLabel)
        {
            // Retry several times as mounting can take a few seconds after a write.
            for (int i = 0; i < 10; ++i)
            {
                try
                {
                    DriveInfo[] drives = DriveInfo.GetDrives();
                    foreach (DriveInfo drive in drives)
                    {
                        if (drive.IsReady && string.Equals(drive.VolumeLabel, volumeLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.WriteLine($"ImageWriter: Found volume '{volumeLabel}' at {drive.RootDirectory.FullName}.");
                            return drive.RootDirectory.FullName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ImageWriter.WaitForVolumeMount: Error getting drives on attempt {i + 1}: {ex.Message}");
                }

                Debug.WriteLine($"ImageWriter: Volume '{volumeLabel}' not yet mounted. Waiting... ({i + 1}/10)");
                await Task.Delay(2000); // Wait 2 seconds before retrying.
            }
            return null;
        }

        #endregion
    }
}