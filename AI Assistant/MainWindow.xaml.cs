using AI_Assistant.AI;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AI_Assistant
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<ChatEntry> messages =
            new ObservableCollection<ChatEntry>();

        private AssistantRuntime? ai;
        private bool isBusy;
        private string latestActivity = "Idle";

        public MainWindow()
        {
            InitializeComponent();
            MessagesList.ItemsSource = messages;
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ai = CreateAgent();
                ai.Activity += OnAgentActivity;

                SetStatus(
                    "Spreman · " + AgentVersion.Version,
                    Color.FromRgb(69, 201, 142)
                );

                RefreshLiveInspector();

                AddMessage(
                    "Assistant",
                    "Cowork SHIP V1 je spreman. Unity koristi Agent V2, /blender koristi controlled Blender pipeline, a runtime prikazuje rad agenta u Live Inspectoru bez zatrpavanja chata."
                );

                PromptTextBox.Focus();
            }
            catch (Exception ex)
            {
                SetStatus("Greška pri pokretanju", Color.FromRgb(239, 95, 95));
                AddMessage("System", ex.GetType().Name + ": " + ex.Message);
                PromptTextBox.IsEnabled = false;
                SendButton.IsEnabled = false;
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendCurrentPromptAsync();
        }

        private async void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                await SendCurrentPromptAsync();
            }
        }

        private async Task SendCurrentPromptAsync()
        {
            if (isBusy || ai == null)
            {
                return;
            }

            string prompt = PromptTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            PromptTextBox.Clear();
            AddMessage("User", prompt);
            latestActivity = "Starting task";
            SetBusy(true);
            RefreshLiveInspector();

            try
            {
                string answer = await Task.Run(() => ai.Ask(prompt));

                if (string.IsNullOrWhiteSpace(answer))
                {
                    AddMessage(
                        "System",
                        "Agent nije vratio tekstualni odgovor. Zadatak nije potvrđen kao završen."
                    );
                }
                else
                {
                    AddMessage("Assistant", answer);
                }
            }
            catch (Exception ex)
            {
                AddMessage("System", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                latestActivity = "Ready";
                SetBusy(false);
                RefreshLiveInspector();
                PromptTextBox.Focus();
            }
        }

        private void SetBusy(bool busy)
        {
            isBusy = busy;
            SendButton.IsEnabled = !busy;
            PromptTextBox.IsEnabled = !busy;

            SetStatus(
                busy ? "Agent radi..." : "Spreman · " + AgentVersion.Version,
                busy
                    ? Color.FromRgb(240, 180, 41)
                    : Color.FromRgb(69, 201, 142)
            );
        }

        private void OnAgentActivity(string message)
        {
            void Apply()
            {
                latestActivity = FormatActivity(message);

                if (isBusy)
                {
                    SetStatus(
                        "Agent radi · " + ActivityStage(message),
                        Color.FromRgb(240, 180, 41)
                    );
                }

                RefreshLiveInspector();
            }

            if (Dispatcher.CheckAccess())
            {
                Apply();
                return;
            }

            Dispatcher.BeginInvoke((Action)Apply);
        }

        private void RefreshLiveInspector()
        {
            if (ai == null)
            {
                return;
            }

            string diagnostics = ai.BuildDiagnostics();

            RuntimeDiagnosticsText.Text =
                diagnostics
                + "\n\n"
                + (isBusy ? "ACTIVE TASK" : "LAST STATE")
                + "\n"
                + latestActivity;
        }

        private static string FormatActivity(string message)
        {
            string raw = (message ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Working";
            }

            if (raw.StartsWith("[V2 MODEL]", StringComparison.OrdinalIgnoreCase))
            {
                return "Reasoning · " + TrimPrefix(raw, "[V2 MODEL]");
            }

            if (raw.StartsWith("[V2 TOKENS]", StringComparison.OrdinalIgnoreCase))
            {
                return "Reasoning · context prepared";
            }

            if (raw.StartsWith("[V2 INSPECT]", StringComparison.OrdinalIgnoreCase))
            {
                return "Inspecting Unity · " + TrimPrefix(raw, "[V2 INSPECT]");
            }

            if (raw.StartsWith("[V2 PROVIDER]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 RATE LIMIT]", StringComparison.OrdinalIgnoreCase))
            {
                return "Provider fallback · " + raw[(raw.IndexOf(']') + 1)..].Trim();
            }

            if (raw.StartsWith("[V2 WRITE]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 COMPILE]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 ATTACH]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 ACTION]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 BATCH]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 SAVE]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 TEMP]", StringComparison.OrdinalIgnoreCase))
            {
                return "Executing Unity · " + raw[(raw.IndexOf(']') + 1)..].Trim();
            }

            if (raw.StartsWith("[V2 VERIFY]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 OBSERVE]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[V2 RUNTIME]", StringComparison.OrdinalIgnoreCase))
            {
                return "Verifying Unity · " + raw[(raw.IndexOf(']') + 1)..].Trim();
            }

            if (raw.StartsWith("[BLENDER REPAIR]", StringComparison.OrdinalIgnoreCase))
            {
                return "Repairing Blender run · " + TrimPrefix(raw, "[BLENDER REPAIR]");
            }

            if (raw.StartsWith("[BLENDER VERIFY]", StringComparison.OrdinalIgnoreCase))
            {
                return "Verifying Blender · " + TrimPrefix(raw, "[BLENDER VERIFY]");
            }

            if (raw.StartsWith("[BLENDER]", StringComparison.OrdinalIgnoreCase))
            {
                string detail = TrimPrefix(raw, "[BLENDER]");
                return detail.Contains("execut", StringComparison.OrdinalIgnoreCase)
                    ? "Executing Blender · " + detail
                    : "Preparing Blender · " + detail;
            }

            return raw;
        }

        private static string ActivityStage(string message)
        {
            string raw = (message ?? "").ToUpperInvariant();

            if (raw.Contains("VERIFY") || raw.Contains("OBSERVE"))
            {
                return "Verify";
            }

            if (raw.Contains("EXECUT")
                || raw.Contains("BATCH")
                || raw.Contains("ACTION")
                || raw.Contains("WRITE")
                || raw.Contains("COMPILE")
                || raw.Contains("ATTACH")
                || raw.Contains("SAVE"))
            {
                return "Execute";
            }

            if (raw.Contains("INSPECT"))
            {
                return "Inspect";
            }

            if (raw.Contains("REPAIR") || raw.Contains("CORRECT"))
            {
                return "Repair";
            }

            return "Reason";
        }

        private static string TrimPrefix(string value, string prefix)
        {
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(prefix.Length).Trim()
                : value;
        }

        private void AddMessage(string role, string text)
        {
            messages.Add(new ChatEntry(role, text));
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => MessagesScroll.ScrollToEnd())
            );
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                return;
            }

            ai?.ResetConversationContext();
            messages.Clear();
            latestActivity = "Ready";
            RefreshLiveInspector();
            AddMessage("Assistant", "Razgovor i kontekst zadatka su očišćeni.");
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || ai == null)
            {
                return;
            }

            SettingsWindow window = new SettingsWindow(ai.Settings)
            {
                Owner = this
            };
            window.ShowDialog();
            RefreshLiveInspector();
        }

        private void SetStatus(string text, Color color)
        {
            StatusText.Text = text;
            StatusDot.Fill = new SolidColorBrush(color);
        }

        private static AssistantRuntime CreateAgent()
        {
            string projectFile =
                FindProjectFileUpwards(AppContext.BaseDirectory, "AI Assistant.csproj")
                ?? throw new FileNotFoundException(
                    "AI Assistant.csproj nije pronađen. Pokreni aplikaciju iz build outputa projekta."
                );

            string sourceRoot = Path.GetDirectoryName(projectFile)
                ?? throw new DirectoryNotFoundException("Source root nije pronađen.");

            string solutionRoot = Directory.GetParent(sourceRoot)?.FullName
                ?? throw new DirectoryNotFoundException("Solution root nije pronađen.");

            string updaterProject = Path.Combine(
                solutionRoot,
                "AI Assistant Updater",
                "AI Assistant Updater.csproj"
            );

            if (!File.Exists(updaterProject))
            {
                throw new FileNotFoundException(
                    "Updater project nije pronađen: " + updaterProject
                );
            }

            List<string> allowedRoots = new List<string>();
            string[] optionalRoots =
            {
                @"C:\AIWorkspace",
                @"C:\BlenderProjects",
                @"C:\SubstanceProjects"
            };

            foreach (string root in optionalRoots)
            {
                if (Directory.Exists(root))
                {
                    allowedRoots.Add(root);
                }
            }

            allowedRoots.Add(sourceRoot);

            return new AssistantRuntime(
                allowedRoots,
                projectFile,
                sourceRoot,
                updaterProject
            );
        }

        private static string? FindProjectFileUpwards(
            string startDirectory,
            string projectFileName
        )
        {
            DirectoryInfo? directory = new DirectoryInfo(startDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, projectFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }

    public sealed class ChatEntry
    {
        public string Role { get; }
        public string Text { get; }

        public string DisplayRole =>
            Role == "Assistant" ? "AI" : Role.ToUpperInvariant();

        public ChatEntry(string role, string text)
        {
            Role = role;
            Text = text;
        }
    }
}
