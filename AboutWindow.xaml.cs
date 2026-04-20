using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Navigation;

namespace WhisperInk
{
    public partial class AboutWindow : Window
    {
        public const string ReadmeUrl = "https://github.com/praxeo/whisperinc/blob/main/README.md";

        public AboutWindow()
        {
            InitializeComponent();

            string info = SupportBundle.GetInformationalVersion();
            string hash = SupportBundle.GetCommitHash();
            var build  = SupportBundle.GetBuildDate();

            lblVersion.Text = SupportBundle.GetFileVersion();
            lblCommit.Text  = string.IsNullOrEmpty(hash) ? "(not available)" : hash;
            lblBuild.Text   = build == System.DateTime.MinValue
                ? "(unknown)"
                : build.ToString("yyyy-MM-dd HH:mm");
            lblRuntime.Text = RuntimeInformation.FrameworkDescription;
            lblOs.Text      = RuntimeInformation.OSDescription;
        }

        private void Readme_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(ReadmeUrl) { UseShellExecute = true }); }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
