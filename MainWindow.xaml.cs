using ROCKNIXImageBurner.Models;
using ROCKNIXImageBurner.Properties;
using ROCKNIXImageBurner.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ROCKNIXImageBurner
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. This class contains the UI logic,
    /// event handlers, and orchestrates the image fetching, downloading, and writing processes.
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region Private Fields

        private readonly ImageFetcher _imageFetcher;
        private readonly DriveDetector _driveDetector;
        private readonly ImageDownloader _imageDownloader;
        private readonly ImageWriter _imageWriter;

        /// <summary>
        /// Caches the complete list of all images fetched from the remote source.
        /// </summary>
        private List<ImageInfo> _allImages = new List<ImageInfo>();

        /// <summary>
        /// Stores the path to the last downloaded image file. This is used to avoid re-downloading
        /// the same file if the write operation is attempted again. It is cleared when the selection changes.
        /// </summary>
        private string _downloadedImagePath = null;

        // Fields to preserve user selection when ComboBox sources are refreshed.
        // This provides a smoother user experience by not resetting their choices.
        private string _manufacturerToRestore;
        private string _deviceOriginalNameToRestore;

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Notifies the UI that a property value has changed.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Public Properties for Data Binding

        // These collections are bound to the ComboBoxes in the UI.
        public ObservableCollection<string> AvailableBranches { get; set; }
        public ObservableCollection<string> AvailableManufacturers { get; set; }
        public ObservableCollection<ImageInfo> AvailableDevices { get; set; }
        public ObservableCollection<DriveData> AvailableDrives { get; set; }

        private string _selectedBranch;
        public string SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                if (_selectedBranch != value)
                {
                    _selectedBranch = value;
                    OnPropertyChanged(nameof(SelectedBranch));

                    // When the branch changes, we must update the list of manufacturers.
                    // We save the current selections to attempt to restore them after the update.
                    _manufacturerToRestore = _selectedManufacturer;
                    _deviceOriginalNameToRestore = _selectedDevice?.OriginalName;

                    UpdateManufacturers();
                }
            }
        }

        private string _selectedManufacturer;
        public string SelectedManufacturer
        {
            get => _selectedManufacturer;
            set
            {
                // The check 'if (_selectedManufacturer != value)' is intentionally removed.
                // When the branch changes, the manufacturer name might stay the same (e.g., "Retroid"),
                // but the list of devices for that manufacturer MUST be updated for the new branch.
                // Always running UpdateDevices() here ensures the device list is correctly refreshed.
                _selectedManufacturer = value;
                OnPropertyChanged(nameof(SelectedManufacturer));
                UpdateDevices();
            }
        }

        private ImageInfo _selectedDevice;
        public ImageInfo SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (_selectedDevice != value)
                {
                    _selectedDevice = value;
                    OnPropertyChanged(nameof(SelectedDevice));
                    // A new device has been selected, so any previously downloaded image is now invalid.
                    _downloadedImagePath = null;
                }
            }
        }

        #endregion

        #region Constructor and Window Events

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Initialize services
            _imageFetcher = new ImageFetcher();
            _driveDetector = new DriveDetector();
            _imageDownloader = new ImageDownloader();
            _imageWriter = new ImageWriter();

            // Initialize collections for data binding
            AvailableBranches = new ObservableCollection<string>();
            AvailableManufacturers = new ObservableCollection<string>();
            AvailableDevices = new ObservableCollection<ImageInfo>();
            AvailableDrives = new ObservableCollection<DriveData>();

            // Set ItemsSource for each ComboBox
            BranchComboBox.ItemsSource = AvailableBranches;
            ManufacturerComboBox.ItemsSource = AvailableManufacturers;
            DeviceComboBox.ItemsSource = AvailableDevices;
            DeviceComboBox.DisplayMemberPath = "Device"; // We want to display the 'Device' property of ImageInfo
            DriveComboBox.ItemsSource = AvailableDrives;
            DriveComboBox.DisplayMemberPath = "DisplayString"; // Use the custom display string from DriveData

            this.Loaded += MainWindow_Loaded;
        }

        /// <summary>
        /// Handles the window's Loaded event to perform initial data loading.
        /// </summary>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SetStatus(Strings.status_init_data);
            SetControlsEnabled(false);

            await LoadAllImageDataAsync();
            await LoadDrivesAsync(isInitialLoad: true);

            SetControlsEnabled(true);
        }

        /// <summary>
        /// Handles the window's Closing event to perform cleanup.
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            // This is primarily for debugging purposes, to know where the downloaded file is.
            if (!string.IsNullOrEmpty(_downloadedImagePath) && File.Exists(_downloadedImagePath))
            {
                Debug.WriteLine($"Application closing. Downloaded file kept at: {_downloadedImagePath}");
            }
            base.OnClosing(e);
        }

        #endregion

        #region Data Loading and UI Updating

        /// <summary>
        /// Fetches the complete list of images from the remote source and populates the branches ComboBox.
        /// </summary>
        private async Task LoadAllImageDataAsync()
        {
            SetStatus(Strings.status_fetching_img_success);
            try
            {
                _allImages = await _imageFetcher.FetchImagesAsync();
                if (_allImages.Any())
                {
                    PopulateBranches();
                    SetStatus(Strings.status_fetching_img_success);
                }
                else
                {
                    SetStatus(Strings.status_fetching_no_img);
                    MessageBox.Show(Strings.msg_imglist_error, Strings.title_msg_imglist_err, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"{Strings.status_img_fetch_error} {ex.Message}");
                MessageBox.Show($"{Strings.msg_img_load_fail} {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Populates the 'AvailableBranches' collection based on the fetched image data.
        /// It prioritizes placing the "Stable" branch at the top of the list.
        /// </summary>
        private void PopulateBranches()
        {
            AvailableBranches.Clear();
            var allBranchNames = _allImages.Select(img => img.Branch).Distinct().ToList();

            // Ensure "Stable" branch appears first if it exists.
            string stableBranchName = "Stable";
            if (allBranchNames.Contains(stableBranchName))
            {
                AvailableBranches.Add(stableBranchName);
                foreach (var branch in allBranchNames.Where(b => b != stableBranchName).OrderBy(b => b))
                {
                    AvailableBranches.Add(branch);
                }
            }
            else // Otherwise, just add all branches sorted alphabetically.
            {
                foreach (var branch in allBranchNames.OrderBy(b => b))
                {
                    AvailableBranches.Add(branch);
                }
            }

            // Set a default selection.
            if (AvailableBranches.Any())
            {
                BranchComboBox.SelectedItem = AvailableBranches.FirstOrDefault();
            }
            else
            {
                // If there are no branches, clear the dependent ComboBoxes.
                AvailableManufacturers.Clear();
                AvailableDevices.Clear();
            }
        }

        /// <summary>
        /// Updates the 'AvailableManufacturers' collection based on the selected branch.
        /// It attempts to restore the previously selected manufacturer.
        /// </summary>
        private void UpdateManufacturers()
        {
            string preservedManufacturerName = _manufacturerToRestore;
            _manufacturerToRestore = null; // Consume the value.

            AvailableManufacturers.Clear();
            AvailableDevices.Clear();

            if (SelectedBranch == null || !_allImages.Any())
            {
                SelectedManufacturer = null;
                _deviceOriginalNameToRestore = null; // No manufacturer, so no device can be restored.
                return;
            }

            var manufacturers = _allImages
                .Where(img => img.Branch == SelectedBranch)
                .Select(img => img.Manufacturer)
                .Distinct()
                .OrderBy(m => m)
                .ToList();

            foreach (var man in manufacturers)
            {
                AvailableManufacturers.Add(man);
            }

            // Try to restore the previous selection or default to the first item.
            if (preservedManufacturerName != null && AvailableManufacturers.Contains(preservedManufacturerName))
            {
                ManufacturerComboBox.SelectedItem = preservedManufacturerName;
            }
            else if (AvailableManufacturers.Any())
            {
                _deviceOriginalNameToRestore = null; // Manufacturer changed, so old device selection is invalid.
                ManufacturerComboBox.SelectedIndex = 0;
            }
            else
            {
                SelectedManufacturer = null;
                _deviceOriginalNameToRestore = null;
            }
        }

        /// <summary>
        /// Updates the 'AvailableDevices' collection based on the selected manufacturer.
        /// It attempts to restore the previously selected device.
        /// </summary>
        private void UpdateDevices()
        {
            string preservedDeviceOriginalName = _deviceOriginalNameToRestore;
            _deviceOriginalNameToRestore = null; // Consume the value.

            AvailableDevices.Clear();
            if (SelectedBranch == null || SelectedManufacturer == null || !_allImages.Any())
            {
                SelectedDevice = null;
                return;
            }

            var devices = _allImages
                .Where(img => img.Branch == SelectedBranch && img.Manufacturer == SelectedManufacturer)
                .OrderBy(d => d.Device)
                .ToList();

            foreach (var dev in devices)
            {
                AvailableDevices.Add(dev);
            }

            // Try to restore the previous device selection.
            ImageInfo deviceToRestore = AvailableDevices.FirstOrDefault(d => d.OriginalName == preservedDeviceOriginalName);
            if (deviceToRestore != null)
            {
                DeviceComboBox.SelectedItem = deviceToRestore;
            }
            else if (AvailableDevices.Any())
            {
                // Default to the first device if restoration isn't possible.
                DeviceComboBox.SelectedIndex = 0;
            }
            else
            {
                SelectedDevice = null;
            }
        }

        /// <summary>
        /// Scans for removable drives and populates the drives ComboBox.
        /// </summary>
        /// <param name="isInitialLoad">If true, suppresses some UI feedback to avoid noise on startup.</param>
        private async Task LoadDrivesAsync(bool isInitialLoad = false)
        {
            SetStatus(Strings.status_detecting_drives);
            if (!isInitialLoad)
            {
                SetControlsEnabled(false);
            }

            try
            {
                List<DriveData> drives = await Task.Run(() => _driveDetector.GetRemovableDrives());

                AvailableDrives.Clear();
                if (drives.Any())
                {
                    foreach (var drive in drives)
                    {
                        AvailableDrives.Add(drive);
                    }
                    if (DriveComboBox.Items.Count > 0)
                    {
                        DriveComboBox.SelectedIndex = 0;
                    }
                    SetStatus(Strings.status_drives_loaded);
                }
                else
                {
                    SetStatus(Strings.status_drivce_un_detected);
                    if (isInitialLoad)
                    {
                        MessageBox.Show(Strings.msg_no_drives_detected, Strings.msg_title_no_drives, MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus($"{Strings.status_detected_drives_err} {ex.Message}");
                MessageBox.Show($"{Strings.msg_drives_detect_err} {ex.Message}", Strings.msg_title_drives_detect_err, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (!isInitialLoad)
                {
                    SetControlsEnabled(true);
                }
            }
        }

        #endregion

        #region UI Event Handlers

        private void BranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BranchComboBox.SelectedItem is string selectedBranch)
            {
                SelectedBranch = selectedBranch;
            }
            _downloadedImagePath = null;
        }

        private void ManufacturerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ManufacturerComboBox.SelectedItem is string selectedManufacturer)
            {
                SelectedManufacturer = selectedManufacturer;
            }
            _downloadedImagePath = null;
        }

        private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem is ImageInfo selectedDevice)
            {
                SelectedDevice = selectedDevice;
            }
            _downloadedImagePath = null;
        }

        /// <summary>
        /// Handles the click event for the 'Refresh Drives' button.
        /// </summary>
        private async void RefreshDrivesButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadProgressBar.Value = 0;
            WriteProgressBar.Value = 0;
            await LoadDrivesAsync();
        }

        /// <summary>
        /// Handles the click event of the 'About' button by creating and showing the AboutWindow.
        /// </summary>
        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            AboutWindow aboutWindow = new AboutWindow();
            aboutWindow.ShowDialog(); // ShowDialog makes the window modal.
        }

        /// <summary>
        /// Handles the click event for the 'Write' button, starting the main workflow.
        /// </summary>
        private async void WriteButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedImageInfo = DeviceComboBox.SelectedItem as ImageInfo;
            var selectedDrive = DriveComboBox.SelectedItem as DriveData;

            // 1. Validate selections
            if (selectedImageInfo == null)
            {
                MessageBox.Show(Strings.msg_img_select_notice, Strings.msg_title_img_select_notice, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (selectedDrive == null)
            {
                MessageBox.Show(Strings.msg_drive_select_notice, Strings.msg_title_drive_select_notice, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. Confirm with the user, as this is a destructive operation.
            var confirmation = MessageBox.Show(
                Strings.msg_write_confirm,
                Strings.msg_title_write_confirm,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
            {
                SetStatus(Strings.status_cancel_by_user);
                return;
            }

            // 3. Start the write process
            SetControlsEnabled(false);
            DownloadProgressBar.Value = 0;
            WriteProgressBar.Value = 0;
            List<string> volumePaths = new List<string>();

            try
            {
                // 3a. Download and verify the image file.
                var downloadProgress = new Progress<double>(p => DownloadProgressBar.Value = p);
                _downloadedImagePath = await _imageDownloader.DownloadAndVerifyAsync(selectedImageInfo, downloadProgress, SetStatus);
                DownloadProgressBar.Value = 100;

                // 3b. Prepare for writing by getting volume paths for dismounting. 
                SetStatus(string.Format(Strings.status_pre_write,selectedImageInfo.OriginalName,selectedDrive.DeviceID));
                volumePaths = await Task.Run(() => _driveDetector.GetVolumePathsForPhysicalDrive(selectedDrive.DeviceID));

                if (string.IsNullOrEmpty(_downloadedImagePath))
                {
                    throw new InvalidOperationException("Downloaded image path is not available, cannot proceed with writing.");
                }

                // 3c. Write the image to the drive.
                SetStatus(Strings.status_writing_not_remove);
                var writeProgress = new Progress<double>(p => WriteProgressBar.Value = p);
                await _imageWriter.WriteImageAsync(_downloadedImagePath, selectedDrive.DeviceID, volumePaths, writeProgress, selectedImageInfo);
                WriteProgressBar.Value = 100;

                SetStatus(Strings.status_write_comp_success);
                MessageBox.Show(Strings.msg_write_success, Strings.msg_title_success, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5 && selectedDrive != null) // Access Denied
            {
                // This is a common and specific error, so provide a detailed, helpful message.
                string detailedMessage = string.Format(Strings.msg_acc_deny_details, selectedDrive.DeviceID);

                if (!volumePaths.Any())
                {
                    detailedMessage += Strings.msg_acc_deny_details_diag;
                }

                SetStatus(string.Format(Strings.status_denied_writing,selectedDrive.DeviceID));
                MessageBox.Show(detailedMessage, Strings.msg_title_denied_writing, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Win32Exception ex) // Other system errors
            {
                string driveId = selectedDrive?.DeviceID ?? "the selected drive";          
                string detailedMessage =string.Format(Strings.msg_w32_write_err,driveId,ex.Message,ex.NativeErrorCode);

                SetStatus($"ERROR: {ex.Message} (Code: {ex.NativeErrorCode}) on drive {driveId}");
                MessageBox.Show(detailedMessage, Strings.msg_title_sys_w_err, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex) // All other errors
            {
                SetStatus($"ERROR: {ex.Message}");
                MessageBox.Show($"{Strings.msg_unexpected_err} {ex.Message}", Strings.title_operate_fail, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 4. Cleanup and reset UI state.
                if (!string.IsNullOrEmpty(_downloadedImagePath))
                {
                    Debug.WriteLine($"Write operation finished. Downloaded file kept at: {_downloadedImagePath}");
                }

                SetControlsEnabled(true);
                await LoadDrivesAsync(); // Refresh drive list as partitions have changed.
            }
        }

        #endregion

        #region UI State Management

        /// <summary>
        /// Sets the message in the status bar at the bottom of the window.
        /// </summary>
        /// <param name="message">The message to display.</param>
        private void SetStatus(string message)
        {
            StatusTextBlock.Text = message;
            Debug.WriteLine($"Status: {message}");
        }

        /// <summary>
        /// Enables or disables all major user controls in the window.
        /// </summary>
        /// <param name="isEnabled">True to enable controls, false to disable.</param>
        private void SetControlsEnabled(bool isEnabled)
        {
            BranchComboBox.IsEnabled = isEnabled;
            ManufacturerComboBox.IsEnabled = isEnabled;
            DeviceComboBox.IsEnabled = isEnabled;
            DriveComboBox.IsEnabled = isEnabled;
            RefreshDrivesButton.IsEnabled = isEnabled;
            WriteButton.IsEnabled = isEnabled;
        }

        #endregion
    }
}
