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
        private readonly ObservableCollection<ChatEntry>
            messages =
                new ObservableCollection<ChatEntry>();

        private AssistantRuntime? ai;
        private bool isBusy;


        public MainWindow()
        {
            InitializeComponent();

            MessagesList.ItemsSource = messages;

            Loaded += MainWindow_Loaded;
        }


        private void MainWindow_Loaded(
            object sender,
            RoutedEventArgs e
        )
        {
            try
            {
                ai = CreateAgent();

                ai.Activity += OnAgentActivity;

                SetStatus(
                    "Spreman · " + AgentVersion.Version,
                    Color.FromRgb(69, 201, 142)
                );

                AddMessage(
                    "Assistant",
                    "AI Assistant je spreman. Unity zahtjevi koriste Cowork Agent V2; ostali workflow-i ostaju na compatibility routeru."
                );

                PromptTextBox.Focus();
            }
            catch (Exception ex)
            {
                SetStatus(
                    "Greška pri pokretanju",
                    Color.FromRgb(239, 95, 95)
                );

                AddMessage(
                    "System",
                    ex.GetType().Name + ": " + ex.Message
                );

                PromptTextBox.IsEnabled = false;
                SendButton.IsEnabled = false;
            }
        }


        private async void SendButton_Click(
            object sender,
            RoutedEventArgs e
        )
        {
            await SendCurrentPromptAsync();
        }


        private async void PromptTextBox_PreviewKeyDown(
            object sender,
            KeyEventArgs e
        )
        {
            if (
                e.Key == Key.Enter
                && Keyboard.Modifiers != ModifierKeys.Shift
            )
            {
                e.Handled = true;
                await SendCurrentPromptAsync();
            }
        }


        private async Task SendCurrentPromptAsync()
        {
            if (
                isBusy
                || ai == null
            )
            {
                return;
            }

            string prompt =
                PromptTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            PromptTextBox.Clear();
            AddMessage("User", prompt);

            SetBusy(true);

            try
            {
                string answer =
                    await Task.Run(
                        () => ai.Ask(prompt)
                    );

                if (
                    string.IsNullOrWhiteSpace(
                        answer
                    )
                )
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
                AddMessage(
                    "System",
                    ex.GetType().Name + ": " + ex.Message
                );
            }
            finally
            {
                SetBusy(false);
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
            if (Dispatcher.CheckAccess())
            {
                AddMessage("Activity", message);
                return;
            }

            Dispatcher.Invoke(
                () => AddMessage("Activity", message)
            );
        }


        private void AddMessage(
            string role,
            string text
        )
        {
            messages.Add(
                new ChatEntry(role, text)
            );

            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(
                    () => MessagesScroll.ScrollToEnd()
                )
            );
        }


        private void ClearButton_Click(
            object sender,
            RoutedEventArgs e
        )
        {
            if (isBusy)
            {
                return;
            }

            ai?.ResetConversationContext();

            messages.Clear();

            AddMessage(
                "Assistant",
                "Razgovor i kontekst zadatka su očišćeni."
            );
        }


        private void SetStatus(
            string text,
            Color color
        )
        {
            StatusText.Text = text;
            StatusDot.Fill = new SolidColorBrush(color);
        }


        private static AssistantRuntime CreateAgent()
        {
            string projectFile =
                FindProjectFileUpwards(
                    AppContext.BaseDirectory,
                    "AI Assistant.csproj"
                )
                ?? throw new FileNotFoundException(
                    "AI Assistant.csproj nije pronađen. Pokreni aplikaciju iz build outputa projekta."
                );

            string sourceRoot =
                Path.GetDirectoryName(projectFile)
                ?? throw new DirectoryNotFoundException(
                    "Source root nije pronađen."
                );

            string solutionRoot =
                Directory.GetParent(sourceRoot)?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Solution root nije pronađen."
                );

            string updaterProject =
                Path.Combine(
                    solutionRoot,
                    "AI Assistant Updater",
                    "AI Assistant Updater.csproj"
                );

            if (!File.Exists(updaterProject))
            {
                throw new FileNotFoundException(
                    "Updater project nije pronađen: "
                    + updaterProject
                );
            }

            List<string> allowedRoots =
                new List<string>();

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

            return
                new AssistantRuntime(
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
            DirectoryInfo? directory =
                new DirectoryInfo(startDirectory);

            while (directory != null)
            {
                string candidate =
                    Path.Combine(
                        directory.FullName,
                        projectFileName
                    );

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
            Role == "Assistant"
                ? "AI"
                : Role.ToUpperInvariant();


        public ChatEntry(
            string role,
            string text
        )
        {
            Role = role;
            Text = text;
        }
    }
}
