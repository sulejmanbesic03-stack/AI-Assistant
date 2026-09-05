using AI_Assistant.Runtime;

using System;
using System.Linq;
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
            PreferFreeCheckBox.IsChecked = settings.PreferFreeProviders;
            ApprovalCheckBox.IsChecked = settings.RequireApprovalForDestructiveChanges;
            MaxCallsTextBox.Text = settings.MaxModelCallsPerTask.ToString();
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
                "Settings saved. New Blender tasks use them immediately. Restart the app if you changed provider-related environment variables outside this window.",
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
            settings.PreferFreeProviders = PreferFreeCheckBox.IsChecked != false;
            settings.RequireApprovalForDestructiveChanges = ApprovalCheckBox.IsChecked != false;

            if (int.TryParse(MaxCallsTextBox.Text, out int calls))
            {
                settings.MaxModelCallsPerTask = Math.Clamp(calls, 1, 20);
            }
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
