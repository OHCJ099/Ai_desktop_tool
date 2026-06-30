using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AI_desktop_tool
{
    public partial class MainWindow : Window
    {
        private AppConfig _config = new AppConfig();
        private bool _isRequesting = false;
        private bool _isSettingsMode = false;
        private System.Windows.Threading.DispatcherTimer _topmostTimer;
        private System.Windows.Threading.DispatcherTimer _foregroundTrackerTimer;
        private IntPtr _lastExternalForeground = IntPtr.Zero;
        private IntPtr _lastExternalFocus = IntPtr.Zero;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Maintain a separate CDP-enabled exam client shortcut.
            // This does not start the exam client; it only creates/repairs the shortcut if needed.
            ExamShortcutHelper.EnsureDesktopShortcut();

            // Load saved config
            _config = ConfigHelper.LoadConfig();
            ApplyConfig();
            ApplyDisplayAffinity();

            // Set initial position (top-right area of primary screen)
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            this.Left = screenWidth - this.Width - 50;
            this.Top = 80;

            // Initialize topmost timer to force overlay priority
            _topmostTimer = new System.Windows.Threading.DispatcherTimer();
            _topmostTimer.Interval = TimeSpan.FromMilliseconds(500);
            _topmostTimer.Tick += (s, ev) => ForceWindowTopmost();
            _topmostTimer.Start();

            _foregroundTrackerTimer = new System.Windows.Threading.DispatcherTimer();
            _foregroundTrackerTimer.Interval = TimeSpan.FromMilliseconds(120);
            _foregroundTrackerTimer.Tick += (s, ev) => TrackExternalForegroundWindow();
            _foregroundTrackerTimer.Start();
        }

        private void ApplyDisplayAffinity()
        {
            var wih = new System.Windows.Interop.WindowInteropHelper(this);
            uint affinity = _config.EnableAntiCapture ? Win32Helper.WDA_EXCLUDEFROMCAPTURE : Win32Helper.WDA_NONE;
            Win32Helper.SetWindowDisplayAffinity(wih.Handle, affinity);
        }

        private void ApplyConfig()
        {
            // Set font weight
            FontWeight weight = FontWeights.Normal;
            switch (_config.FontWeight)
            {
                case "Bold":
                    weight = FontWeights.Bold;
                    break;
                case "SemiBold":
                    weight = FontWeights.SemiBold;
                    break;
                case "Thin":
                    weight = FontWeights.Thin;
                    break;
                default:
                    weight = FontWeights.Normal;
                    break;
            }

            // Apply style to elements
            QueryTextBox.FontWeight = weight;
            SendButton.FontWeight = weight;
            TypeButton.FontWeight = weight;
            CefCaptureButton.FontWeight = weight;
            SettingsButton.FontWeight = weight;
            ExitButton.FontWeight = weight;
            AnswerTextBlock.FontWeight = weight;

            // Trigger mouse-leave state initially (fully transparent)
            MainContentGrid.Opacity = 0.0;
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            // Show content with the configured opacity when mouse enters
            MainContentGrid.Opacity = _config.TextOpacity / 100.0;
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            // Hide content completely when mouse leaves
            MainContentGrid.Opacity = 0.0;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Allow dragging from any empty space (grid background)
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    this.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignore DragMove exception if the mouse button was already released
                }
            }
        }

        private void TrackExternalForegroundWindow()
        {
            try
            {
                var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                IntPtr fg = Win32Helper.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == self) return;

                _lastExternalForeground = fg;
                uint threadId = Win32Helper.GetWindowThreadProcessId(fg, out _);
                if (threadId != 0)
                {
                    var info = new Win32Helper.GUITHREADINFO
                    {
                        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Helper.GUITHREADINFO>()
                    };
                    if (Win32Helper.GetGUIThreadInfo(threadId, ref info))
                    {
                        _lastExternalFocus = info.hwndFocus;
                    }
                }
            }
            catch
            {
                // Keep last known values.
            }
        }

        // Text button hover effects
        private void Button_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock tb)
            {
                tb.Foreground = Brushes.Gray; // Dark gray visual cue
            }
        }

        private void Button_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock tb)
            {
                tb.Foreground = Brushes.Black; // Reset to pure black
            }
        }

        // Action: S (Send Request)
        private async void SendButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                await TriggerSendAsync();
            }
        }

        private async void QueryTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await TriggerSendAsync();
            }
        }

        private async Task TriggerSendAsync()
        {
            if (_isRequesting) return;

            string prompt = QueryTextBox.Text.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            _isRequesting = true;
            SendButton.Text = "...";
            AnswerTextBlock.Text = "思考中...";

            try
            {
                string answer = await SendApiRequestAsync(prompt);
                AnswerTextBlock.Text = answer;
            }
            catch (Exception ex)
            {
                AnswerTextBlock.Text = $"请求发生错误: {ex.Message}";
            }
            finally
            {
                SendButton.Text = "S";
                _isRequesting = false;
            }
        }

        private async Task<string> SendApiRequestAsync(string prompt)
        {
            string url = _config.ApiUrl;
            string requestUrl = url.EndsWith("/") ? url + "chat/completions" : url + "/chat/completions";

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.ApiKey}");

                var requestBody = new
                {
                    model = _config.ModelName,
                    messages = new[]
                    {
                        new { role = "system", content = _config.SystemPrompt },
                        new { role = "user", content = prompt }
                    }
                };

                string jsonPayload = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(requestUrl, content);
                string responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(responseText))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var message = choices[0].GetProperty("message");
                            return message.GetProperty("content").GetString() ?? string.Empty;
                        }
                    }
                    return "Error: 无法解析 API 返回的 JSON 结构。";
                }
                else
                {
                    return $"请求失败 ({response.StatusCode}):\n{responseText}";
                }
            }
        }



        // Action: P (Keyboard Simulation Typing Output)
        private async void TypeButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                string answerText = AnswerTextBlock.Text;
                if (string.IsNullOrEmpty(answerText)) return;

                // Hide floating window to restore focus to the previously active application
                this.Hide();

                // Prefer direct CEF DOM injection. It does not depend on Windows focus,
                // IME, clipboard, or the caret surviving after clicking the overlay.
                bool injected = false;
                try
                {
                    injected = await CefDomExtractor.TryInjectAnswerAsync(answerText, 9222);
                }
                catch
                {
                    injected = false;
                }

                if (injected)
                {
                    this.Show();
                    this.Topmost = true;
                    return;
                }

                try
                {
                    string diagPath = await CefDomExtractor.DumpInjectionDiagnosticsAsync(9222);
                    AnswerTextBlock.Text = answerText + $"\n\n[注入失败，已导出诊断]\n{diagPath}";
                }
                catch (Exception diagEx)
                {
                    AnswerTextBlock.Text = answerText + $"\n\n[注入失败，诊断导出也失败]\n{diagEx.Message}";
                }

                // For CEF exam clients, the fallback keyboard simulation is unreliable and can
                // hide the real failure signal. Stop here so the user always sees either the
                // diagnostic file path or the diagnostic error.
                this.Show();
                this.Topmost = true;
                return;
            }
        }

        // Action: C (CEF/CDP DOM text extraction)
        private async void CefCaptureButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;

            if (_isRequesting) return;
            _isRequesting = true;
            CefCaptureButton.Text = "...";
            AnswerTextBlock.Text = "正在从 CEF/CDP 读取题面...";

            try
            {
                string extracted = await CefDomExtractor.ExtractBestTextAsync(9222);
                QueryTextBox.Text = extracted;
                QueryTextBox.CaretIndex = QueryTextBox.Text.Length;

                if (!string.IsNullOrWhiteSpace(extracted) &&
                    !extracted.StartsWith("未发现可读取", StringComparison.Ordinal) &&
                    !extracted.StartsWith("已连接 CEF", StringComparison.Ordinal))
                {
                    string answer = await SendApiRequestAsync(extracted);
                    AnswerTextBlock.Text = answer;
                }
                else
                {
                    AnswerTextBlock.Text = extracted;
                }
            }
            catch (Exception ex)
            {
                AnswerTextBlock.Text = $"CEF 抽题失败: {ex.Message}";
            }
            finally
            {
                CefCaptureButton.Text = "C";
                _isRequesting = false;
            }
        }

        // Action: 设置 (Open Settings Panel)
        private void SettingsButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                ToggleSettingsMode();
            }
        }

        // Action: 退出 (Exit Application)
        private void ExitButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                if (_isSettingsMode)
                {
                    CancelSettingsMode();
                }
                else
                {
                    Application.Current.Shutdown();
                }
            }
        }

        private void ToggleSettingsMode()
        {
            if (!_isSettingsMode)
            {
                _isSettingsMode = true;
                SettingsButton.Text = "√";
                ExitButton.Text = "x";
                
                LoadConfigToInlineUI();
                
                AnswerScrollViewer.Visibility = Visibility.Collapsed;
                SettingsScrollViewer.Visibility = Visibility.Visible;
                
                this.Height = 320;
            }
            else
            {
                SaveConfigFromInlineUI();
                
                _isSettingsMode = false;
                SettingsButton.Text = ".";
                ExitButton.Text = "x";
                
                _config = ConfigHelper.LoadConfig();
                ApplyConfig();
                ApplyDisplayAffinity();
                
                SettingsScrollViewer.Visibility = Visibility.Collapsed;
                AnswerScrollViewer.Visibility = Visibility.Visible;
                
                this.Height = 160;
            }
        }

        private void CancelSettingsMode()
        {
            _isSettingsMode = false;
            SettingsButton.Text = ".";
            ExitButton.Text = "x";
            
            // 重新从 config.json 加载配置，使通过外部记事本编辑的更改生效
            _config = ConfigHelper.LoadConfig();
            ApplyConfig();
            ApplyDisplayAffinity();
            
            SettingsScrollViewer.Visibility = Visibility.Collapsed;
            AnswerScrollViewer.Visibility = Visibility.Visible;
            
            this.Height = 160;
        }

        private void LoadConfigToInlineUI()
        {
            SetApiUrlTextBox.Text = _config.ApiUrl;
            SetApiKeyTextBox.Text = _config.ApiKey;
            SetModelTextBox.Text = _config.ModelName;
            SetPromptTextBox.Text = _config.SystemPrompt;
            
            OpacitySlider.Value = _config.TextOpacity;
            DelaySlider.Value = _config.TypeDelayMs;
            AntiCaptureCheckBox.IsChecked = _config.EnableAntiCapture;

            string currentWeight = _config.FontWeight ?? "Normal";
            bool foundWeight = false;
            foreach (ComboBoxItem item in FontWeightComboBox.Items)
            {
                if (item.Content.ToString().Equals(currentWeight, StringComparison.OrdinalIgnoreCase))
                {
                    FontWeightComboBox.SelectedItem = item;
                    foundWeight = true;
                    break;
                }
            }
            if (!foundWeight && FontWeightComboBox.Items.Count > 0)
            {
                FontWeightComboBox.SelectedIndex = 0;
            }
        }

        private void SaveConfigFromInlineUI()
        {
            _config.ApiUrl = SetApiUrlTextBox.Text.Trim();
            _config.ApiKey = SetApiKeyTextBox.Text.Trim();
            _config.ModelName = SetModelTextBox.Text.Trim();
            _config.SystemPrompt = SetPromptTextBox.Text;
            
            _config.TextOpacity = OpacitySlider.Value;
            _config.TypeDelayMs = (int)DelaySlider.Value;
            _config.EnableAntiCapture = AntiCaptureCheckBox.IsChecked == true;

            if (FontWeightComboBox.SelectedItem is ComboBoxItem selectedWeightItem)
            {
                _config.FontWeight = selectedWeightItem.Content.ToString();
            }
            else
            {
                _config.FontWeight = "Normal";
            }

            ConfigHelper.SaveConfig(_config);
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (this.IsVisible)
            {
                this.Topmost = false;
                this.Topmost = true;
                ForceWindowTopmost();
            }
        }

        private void ForceWindowTopmost()
        {
            if (!this.IsVisible) return;
            try
            {
                var wih = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hwnd = wih.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Win32Helper.SetWindowPos(hwnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0,
                        Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
                }
            }
            catch
            {
                // Ignore
            }
        }

        public static void LogDebug(string message)
        {
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");
                string logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                System.IO.File.AppendAllText(logPath, logMsg);
            }
            catch
            {
                // Ignore
            }
        }
    }
}
