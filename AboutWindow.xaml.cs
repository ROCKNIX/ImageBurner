using System.Reflection;
using System.Windows;

namespace ROCKNIXImageBurner
{
    /// <summary>
    /// Interaction logic for AboutWindow.xaml.
    /// This window displays information about the application.
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            // Set the owner of this window to the main application window.
            // This ensures it appears centered over the main window and is modal to it.
            this.Owner = Application.Current.MainWindow;
            LoadAssemblyInformation();
        }

        /// <summary>
        /// Loads information from the assembly metadata (e.g., version, title)
        /// and populates the TextBlocks in the window.
        /// </summary>
        private void LoadAssemblyInformation()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;

            // Get Title
            var titleAttr = assembly.GetCustomAttribute<AssemblyTitleAttribute>();
            if (titleAttr != null)
            {
                TitleTextBlock.Text = titleAttr.Title;
                this.Title = $"About {titleAttr.Title}"; // Set window title as well
            }

            // Get Version and format it to show Major.Minor.Build (e.g., "1.0.0")
            VersionTextBlock.Text = $"Version: {version.ToString(3)}";

            // Get Description
            var descriptionAttr = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>();
            if (descriptionAttr != null)
            {
                DescriptionTextBlock.Text = descriptionAttr.Description;
            }
        }

        /// <summary>
        /// Handles the click event for the Close button.
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}