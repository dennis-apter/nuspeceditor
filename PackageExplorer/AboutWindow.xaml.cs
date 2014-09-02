using System;
using System.Deployment.Application;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using StringResources = PackageExplorer.Resources.Resources;

namespace PackageExplorer
{
    /// <summary>
    /// Interaction logic for AboutWindow.xaml
    /// </summary>
    public partial class AboutWindow
    {
        public AboutWindow()
        {
            InitializeComponent();

#if NUSPEC_EDITOR
            ProductTitle.Text = String.Format(
                CultureInfo.CurrentCulture,
                "{0} ({1})",
                "NuSpec Editor",
                GetApplicationVersion());
#else
            ProductTitle.Text = String.Format(
                CultureInfo.CurrentCulture,
                "{0} ({1})",
                StringResources.Dialog_Title,
                GetApplicationVersion());
#endif
        }

        private static Version GetApplicationVersion()
        {
            return ApplicationDeployment.IsNetworkDeployed ? 
                ApplicationDeployment.CurrentDeployment.CurrentVersion : 
                typeof(MainWindow).Assembly.GetName().Version;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Hyperlink_Click(object sender, RoutedEventArgs e)
        {
            var link = (Hyperlink) sender;
            UriHelper.OpenExternalLink(link.NavigateUri);
        }
    }
}