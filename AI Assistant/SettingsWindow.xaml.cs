using AI_Assistant.Runtime;

using System.Windows;

namespace AI_Assistant
{
    public partial class SettingsWindow : Window
    {
        private readonly RuntimeSettings settings;

        public SettingsWindow(RuntimeSettings settings)
        {
            InitializeComponent();
            this.settings = settings;

            UnityRootTextBox.Text = settings.UnityProjectRoot;
            BlenderExeTextBox.Text = settings.BlenderExecutable;
            BlenderWorkspaceTextBox.Text = settings.BlenderWorkspace;
            ApprovalCheckBox.IsChecked = settings.RequireApprovalForDestructiveChanges;
            RefreshValidation();
        }

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            CopyFromControls();
            RefreshValidation();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            CopyFromControls();
            settings.Save();
            settings.ApplyToProcessEnvironment();
            RefreshValidation();
            MessageBox.Show(
                this,
                "Settings saved. New Blender tasks and risk-gate decisions use them immediately.",
                "AI Assistant",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        private void CopyFromControls()
        {
            settings.UnityProjectRoot = UnityRootTextBox.Text;
            settings.BlenderExecutable = BlenderExeTextBox.Text;
            settings.BlenderWorkspace = BlenderWorkspaceTextBox.Text;
            settings.RequireApprovalForDestructiveChanges = ApprovalCheckBox.IsChecked != false;
        }

        private void RefreshValidation()
        {
            var issues = settings.Validate();
            ValidationText.Text = issues.Count == 0
                ? "Runtime validation: OK. Blender and configured Unity project paths are reachable."
                : "Runtime validation:\n• " + string.Join("\n• ", issues);
        }
    }
}
