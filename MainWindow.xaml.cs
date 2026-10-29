using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsClient
{
    public partial class MainWindow : Window
    {
        private const double NormalWindowWidth = 172;
        private const double NormalWindowHeight = 78;
        private const double SettingsWindowHeight = 265;
        private const double SettingsWindowWidth = 340;

        private AppConfig _config = new AppConfig();
        private bool _isRequesting = false;
        private bool _isSettingsMode = false;
        private System.Windows.Threading.DispatcherTimer? _topmostTimer;
        private System.Windows.Threading.DispatcherTimer? _foregroundTrackerTimer;
        private System.Windows.Threading.DispatcherTimer? _keyboardHookWatchdogTimer;
        private IntPtr _lastExternalForeground = IntPtr.Zero;
        private IntPtr _lastExternalFocus = IntPtr.Zero;
        private SystemInput? _ctrlVHook;
        private bool _isHandlingSuppressedCtrlV = false;
        private CancellationTokenSource? _automationCts;
        private bool _automationPaused = false;
        private bool _automationRunning = false;
        private string _lastAutomationStatus = "";
        private System.Windows.Threading.DispatcherTimer? _settingsAutoCloseTimer;
        private bool _suppressOpacitySave = false;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Maintain a separate CDP-enabled exam client shortcut.
            // This does not start the exam client; it only creates/repairs the shortcut if needed.
            ClientLink.EnsureDesktopShortcut();

            // Load saved config
            _config = ConfigHelper.LoadConfig();
            _config.EnableAntiCapture = true;
            ApplyConfig();
            ApplyDisplayAffinity();

            // Set initial position: center of primary screen.
            this.Width = NormalWindowWidth;
            this.Height = NormalWindowHeight;
            CenterWindowOnPrimaryScreen();

            // Initialize topmost timer to force overlay priority
            _topmostTimer = new System.Windows.Threading.DispatcherTimer();
            _topmostTimer.Interval = TimeSpan.FromMilliseconds(300);
            _topmostTimer.Tick += (s, ev) => ForceWindowTopmost();
            _topmostTimer.Start();

            _foregroundTrackerTimer = new System.Windows.Threading.DispatcherTimer();
            _foregroundTrackerTimer.Interval = TimeSpan.FromMilliseconds(100);
            _foregroundTrackerTimer.Tick += (s, ev) => TrackExternalForegroundWindow();
            _foregroundTrackerTimer.Start();

            _ctrlVHook = new SystemInput(() =>
                Dispatcher.InvokeAsync(HandleSuppressedCtrlVAsync).Task.Unwrap());
            _ctrlVHook.Start();
            SystemInput.RefreshSuppressionState();

            _keyboardHookWatchdogTimer = new System.Windows.Threading.DispatcherTimer();
            _keyboardHookWatchdogTimer.Interval = TimeSpan.FromSeconds(5);
            _keyboardHookWatchdogTimer.Tick += (s, ev) =>
            {
                try
                {
                    if (_ctrlVHook != null && !_ctrlVHook.IsInstalled)
                    {
                        _ctrlVHook.Start();
                    }
                }
                catch { }
            };
            _keyboardHookWatchdogTimer.Start();

        }

        protected override void OnClosed(EventArgs e)
        {
            _automationCts?.Cancel();
            _topmostTimer?.Stop();
            _foregroundTrackerTimer?.Stop();
            _settingsAutoCloseTimer?.Stop();
            _keyboardHookWatchdogTimer?.Stop();
            _ctrlVHook?.Dispose();
            _ctrlVHook = null;
            ScheduleCurrentExeDeletion();
            base.OnClosed(e);
        }

        private void CenterWindowOnPrimaryScreen()
        {
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = (SystemParameters.PrimaryScreenHeight - this.Height) / 2;
        }

        private void ApplyDisplayAffinity()
        {
            var wih = new System.Windows.Interop.WindowInteropHelper(this);
            _config.EnableAntiCapture = true;
            NativeMethods.SetWindowDisplayAffinity(wih.Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
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
            LocalSearchButton.FontWeight = weight;
            CefCaptureButton.FontWeight = weight;
            AutoToggleButton.FontWeight = weight;
            SettingsButton.FontWeight = weight;
            ExitButton.FontWeight = weight;
            OutputTextBox.FontWeight = weight;
            DotButton.FontWeight = weight;

            ApplyLiteOpacityConfig();

            // Trigger mouse-leave state initially (fully transparent)
            MainContentGrid.Opacity = 0.0;
        }

        private void ApplyLiteOpacityConfig()
        {
            byte inputAlpha = ToOpacityByte(_config.TextOpacity);
            var inputBrush = new SolidColorBrush(Color.FromArgb(inputAlpha, 0, 0, 0));
            QueryTextBox.Foreground = inputBrush;
            QueryTextBox.CaretBrush = inputBrush;

            DotButton.Foreground = Brushes.Black;
            DotButton.Opacity = ToOpacityRatio(_config.DotOpacity);
            LocalSearchButton.Opacity = DotButton.Opacity;
            SettingsButton.Opacity = DotButton.Opacity;
            ExitButton.Opacity = DotButton.Opacity;
            OutputTextBox.Foreground = inputBrush;
        }

        private static byte ToOpacityByte(double value)
        {
            double clamped = Math.Clamp(value, 0, 100);
            return (byte)Math.Round(255 * clamped / 100.0);
        }

        private static double ToOpacityRatio(double value)
        {
            return Math.Clamp(value, 0, 100) / 100.0;
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            // Show the shell fully; individual elements own their own opacity.
            MainContentGrid.Opacity = 1.0;
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            // Keep settings readable while the user is editing.
            if (!_isSettingsMode) MainContentGrid.Opacity = 0.0;
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
                IntPtr fg = NativeMethods.GetForegroundWindow();
                SystemInput.RefreshSuppressionState();
                if (fg == IntPtr.Zero || fg == self) return;

                _lastExternalForeground = fg;
                uint threadId = NativeMethods.GetWindowThreadProcessId(fg, out _);
                if (threadId != 0)
                {
                    var info = new NativeMethods.GUITHREADINFO
                    {
                        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
                    };
                    if (NativeMethods.GetGUIThreadInfo(threadId, ref info))
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
                if (ReferenceEquals(tb, DotButton)) tb.Opacity = ToOpacityRatio(_config.DotOpacity);
            }
        }

        private void Button_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock tb)
            {
                tb.Foreground = Brushes.Black; // Reset to pure black
                if (ReferenceEquals(tb, DotButton)) tb.Opacity = ToOpacityRatio(_config.DotOpacity);
            }
        }

        // Action: S (Send Request)
        private async void SendButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                await TriggerAiSearchInputOnlyAsync();
            }
        }

        private async void QueryTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await TriggerAiSearchInputOnlyAsync();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (_isSettingsMode && e.Key == Key.Escape)
            {
                e.Handled = true;
                SaveAndCloseSettings();
            }
        }

        private async void LocalSearchButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            await TriggerLocalSearchAsync(captureCurrentPage: true);
        }

        private static void ExitAndDeleteSelfSilently()
        {
            ShutdownSilently();
        }

        private static void ShutdownSilently()
        {
            try
            {
                Application.Current.Shutdown();
            }
            catch
            {
                // Silent exit only.
            }
        }

        private static void ScheduleCurrentExeDeletion()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath)
                    || !System.IO.File.Exists(exePath)
                    || !string.Equals(System.IO.Path.GetFileName(exePath), "jetbra.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string escapedPath = exePath.Replace("'", "''");
                string configPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(exePath) ?? "",
                    "config.json");
                string escapedConfigPath = configPath.Replace("'", "''");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Start-Sleep -Milliseconds 1500; Remove-Item -LiteralPath '" + escapedConfigPath + "' -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '" + escapedPath + "' -Force -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                // Deletion is best-effort and must never block or alert.
            }
        }

        private async Task TriggerLocalSearchAsync(bool captureCurrentPage)
        {
            if (_isRequesting) return;
            _isRequesting = true;
            DotButton.Text = "...";
            OutputTextBox.Text = "\u6b63\u5728\u641c\u7d22...";

            try
            {
                string query = QueryTextBox.Text.Trim();
                string questionType = "";
                List<LocalSearchOption> currentOptions = new();

                if (!captureCurrentPage)
                {
                    QueryTextBox.Text = "";
                    OutputTextBox.Text = "\u672a\u80fd\u8bfb\u53d6\u5f53\u524d\u5185\u5bb9\u3002";
                    return;
                }

                if (captureCurrentPage)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        var snapshot = await ClientSession.GetAutomationSnapshotAsync(9222, cts.Token);
                        query = snapshot.QuestionText?.Trim() ?? "";
                        questionType = snapshot.QuestionType?.Trim() ?? "";
                        currentOptions = snapshot.Options
                            .Where(o => !string.IsNullOrWhiteSpace(o.Key) && !string.IsNullOrWhiteSpace(o.Text))
                            .Select(o => new LocalSearchOption(o.Key, o.Text))
                            .ToList();
                    }
                    catch
                    {
                        query = await ClientSession.ExtractBestTextAsync(9222, cts.Token);
                    }

                    QueryTextBox.Text = query;
                    QueryTextBox.CaretIndex = QueryTextBox.Text.Length;
                }

                if (string.IsNullOrWhiteSpace(query)
                    || query.StartsWith("\u672a\u53d1\u73b0\u53ef\u8bfb\u53d6", StringComparison.Ordinal)
                    || query.StartsWith("\u5df2\u8fde\u63a5", StringComparison.Ordinal))
                {
                    QueryTextBox.Text = "";
                    OutputTextBox.Text = "\u672a\u80fd\u8bfb\u53d6\u5f53\u524d\u5185\u5bb9\u3002";
                    return;
                }

                var match = await LocalQuestionBank.SearchLocalAsync(query, questionType, currentOptions);
                if (match == null)
                {
                    QueryTextBox.Text = "";
                    OutputTextBox.Text = "\u672a\u547d\u4e2d\u3002";
                    return;
                }

                DisplaySearchResult(match.Type, match.Output, currentOptions);
                LogDebug($"Local hit: id={match.Id}; type={match.Type}; score={match.Similarity:0.000}");
            }
            catch (Exception ex)
            {
                QueryTextBox.Text = "";
                OutputTextBox.Text = $"\u5904\u7406\u5931\u8d25: {ex.Message}";
            }
            finally
            {
                DotButton.Text = "S";
                _isRequesting = false;
            }
        }

        private void DisplaySearchResult(string questionType, string output, IReadOnlyList<LocalSearchOption>? currentOptions = null)
        {
            string answer = (output ?? "").Trim();
            OutputTextBox.Text = answer;
            if (!IsChoiceLikeType(NormalizeQuestionType(questionType)))
            {
                TrySetClipboardText(answer);
            }
        }

        private static string NormalizeQuestionType(string type)
        {
            string normalized = (type ?? "").Trim().ToLowerInvariant();
            if (normalized.Contains("single") || normalized.Contains("单选")) return "single";
            if (normalized.Contains("multiple") || normalized.Contains("multi") || normalized.Contains("多选")) return "multiple";
            if (normalized.Contains("judge") || normalized.Contains("判断")) return "judge";
            return normalized;
        }

        private static bool IsChoiceLikeType(string normalizedType)
        {
            return normalizedType == "single" || normalizedType == "multiple" || normalizedType == "judge";
        }

        private static string NormalizeAnswerToChoiceKeys(string normalizedType, string answer, IReadOnlyList<LocalSearchOption>? currentOptions)
        {
            string raw = (answer ?? "").Trim();
            string directKeys = ExtractExplicitChoiceKeys(raw);
            if (!string.IsNullOrEmpty(directKeys))
            {
                return directKeys;
            }

            if (normalizedType == "judge")
            {
                bool? expectedTruth = TryParseJudgeAnswer(raw);
                if (expectedTruth.HasValue)
                {
                    return MapJudgeTruthToCurrentOptionKey(expectedTruth.Value, currentOptions);
                }
            }

            return "";
        }

        private static string ExtractExplicitChoiceKeys(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return "";

            var matches = System.Text.RegularExpressions.Regex.Matches(
                answer,
                @"(?<![A-Za-z])[A-Da-d](?![A-Za-z])");

            if (matches.Count == 0) return "";

            return string.Concat(matches.Select(m => m.Value.ToUpperInvariant()).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        }

        private static bool? TryParseJudgeAnswer(string answer)
        {
            string normalized = (answer ?? "")
                .Replace(" ", "")
                .Replace("\r", "")
                .Replace("\n", "")
                .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(normalized)) return null;

            if (normalized.Contains("正确") || normalized.Contains("对") || normalized.Contains("√") || normalized.Contains("true"))
            {
                return true;
            }

            if (normalized.Contains("错误") || normalized.Contains("错") || normalized.Contains("×") || normalized.Contains("false"))
            {
                return false;
            }

            return null;
        }

        private static string MapJudgeTruthToCurrentOptionKey(bool expectedTruth, IReadOnlyList<LocalSearchOption>? currentOptions)
        {
            if (currentOptions == null || currentOptions.Count == 0) return "";

            foreach (var option in currentOptions)
            {
                if (string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.Text)) continue;

                bool? optionTruth = TryParseJudgeOptionText(option.Text);
                if (optionTruth.HasValue && optionTruth.Value == expectedTruth)
                {
                    string key = option.Key.Trim().ToUpperInvariant();
                    return key.Length > 0 && key[0] >= 'A' && key[0] <= 'D' ? key[0].ToString() : "";
                }
            }

            return "";
        }

        private static bool? TryParseJudgeOptionText(string text)
        {
            string normalized = CleanAnswerToken(text);
            if (string.IsNullOrWhiteSpace(normalized)) return null;

            if (normalized.Contains("正确") || normalized.Contains("对") || normalized.Contains("√") || normalized.Contains("true"))
            {
                return true;
            }

            if (normalized.Contains("错误") || normalized.Contains("错") || normalized.Contains("×") || normalized.Contains("false"))
            {
                return false;
            }

            return null;
        }

        private static string CleanAnswerToken(string text)
        {
            string s = (text ?? "").Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^[A-Ha-h]\s*[\.、．\)）]\s*", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "");
            return s.Trim().ToLowerInvariant();
        }

        private static void TrySetClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // Clipboard may be temporarily locked by another process. The visible \u283b marker is still shown.
            }
        }

        private static async Task<(string QuestionType, List<LocalSearchOption> Options)?> TryGetCurrentChoiceContextAsync(TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                var snapshot = await ClientSession.GetAutomationSnapshotAsync(9222, cts.Token);
                string questionType = snapshot.QuestionType?.Trim() ?? "";
                if (!IsChoiceLikeType(NormalizeQuestionType(questionType)))
                {
                    return null;
                }

                var options = snapshot.Options
                    .Where(o => !string.IsNullOrWhiteSpace(o.Key) && !string.IsNullOrWhiteSpace(o.Text))
                    .Select(o => new LocalSearchOption(o.Key, o.Text))
                    .ToList();

                return options.Count > 0 ? (questionType, options) : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task TriggerAiSearchInputOnlyAsync()
        {
            if (_isRequesting) return;

            string query = QueryTextBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            _isRequesting = true;
            DotButton.Text = "...";
            OutputTextBox.Text = "思考中...";

            try
            {
                string answer = await SendApiRequestAsync(query);
                DisplaySearchResult("single", answer);
            }
            catch (Exception ex)
            {
                QueryTextBox.Text = "";
                OutputTextBox.Text = $"请求发生错误: {ex.Message}";
            }
            finally
            {
                DotButton.Text = "S";
                _isRequesting = false;
            }
        }

        private async Task TriggerSendAsync()
        {
            if (_isRequesting) return;

            string prompt = QueryTextBox.Text.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            _isRequesting = true;
            SendButton.Text = "...";
            OutputTextBox.Text = "思考中...";

            try
            {
                var context = await TryGetCurrentChoiceContextAsync(TimeSpan.FromSeconds(2));
                string answer = await SendApiRequestAsync(prompt);
                if (context != null)
                {
                    DisplaySearchResult(context.Value.QuestionType, answer, context.Value.Options);
                }
                else
                {
                    DisplaySearchResult("", answer);
                }
            }
            catch (Exception ex)
            {
                OutputTextBox.Text = $"请求发生错误: {ex.Message}";
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
                    return "Error: 无法解析返回结构。";
                }
                else
                {
                    return $"请求失败 ({response.StatusCode}):\n{responseText}";
                }
            }
        }
        private async Task HandleSuppressedCtrlVAsync()
        {
            if (_isHandlingSuppressedCtrlV) return;
            _isHandlingSuppressedCtrlV = true;

            try
            {
                string text = "";
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    }
                }
                catch
                {
                    // Clipboard can be temporarily locked; fall back to current answer text.
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = OutputTextBox.Text;
                }
                if (string.IsNullOrWhiteSpace(text)) return;

                // The low-level hook has already swallowed the real Ctrl+V, so the exam page
                // cannot run its "只能录入不能粘贴" branch. Deliver the content as DOM/typed
                // input instead of a paste shortcut.
                for (int i = 0; i < 20 && IsCtrlKeyDown(); i++)
                {
                    await Task.Delay(25);
                }

                // For real Ctrl+V, prefer immediate simulated typing. It is treated as
                // normal input by the exam editor and avoids a slow CDP path that can make
                // the first few attempts look like "no response".
                await TextRelay.SendStringAsync(text, 0);
            }
            finally
            {
                _isHandlingSuppressedCtrlV = false;
            }
        }

        private static bool IsCtrlKeyDown()
        {
            const int VK_CONTROL = 0x11;
            const int VK_LCONTROL = 0xA2;
            const int VK_RCONTROL = 0xA3;
            return (NativeMethods.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                   || (NativeMethods.GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0
                   || (NativeMethods.GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        }

        // Action: C
        private async void CefCaptureButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;

            if (_isRequesting) return;
            _isRequesting = true;
            CefCaptureButton.Text = "...";
            OutputTextBox.Text = "正在读取页面...";

            try
            {
                string extracted;
                string questionType = "";
                List<LocalSearchOption> currentOptions = new();

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    try
                    {
                        var snapshot = await ClientSession.GetAutomationSnapshotAsync(9222, cts.Token);
                        extracted = snapshot.QuestionText?.Trim() ?? "";
                        questionType = snapshot.QuestionType?.Trim() ?? "";
                        currentOptions = snapshot.Options
                            .Where(o => !string.IsNullOrWhiteSpace(o.Key) && !string.IsNullOrWhiteSpace(o.Text))
                            .Select(o => new LocalSearchOption(o.Key, o.Text))
                            .ToList();
                    }
                    catch
                    {
                        extracted = await ClientSession.ExtractBestTextAsync(9222, cts.Token);
                    }
                }

                QueryTextBox.Text = extracted;
                QueryTextBox.CaretIndex = QueryTextBox.Text.Length;

                if (!string.IsNullOrWhiteSpace(extracted) &&
                    !extracted.StartsWith("未发现可读取", StringComparison.Ordinal) &&
                    !extracted.StartsWith("已连接", StringComparison.Ordinal))
                {
                    string answer = await SendApiRequestAsync(extracted);
                    DisplaySearchResult(questionType, answer, currentOptions);
                }
                else
                {
                    OutputTextBox.Text = extracted;
                }
            }
            catch (Exception ex)
            {
                OutputTextBox.Text = $"读取失败: {ex.Message}";
            }
            finally
            {
                CefCaptureButton.Text = "C";
                _isRequesting = false;
            }
        }

        private async void AutoToggleButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;

            if (_automationRunning && !_automationPaused)
            {
                _automationPaused = true;
                SetAutomationStatus("已暂停");
                return;
            }

            if (_automationRunning && _automationPaused)
            {
                _automationPaused = false;
                SetAutomationStatus("自动化中");
                return;
            }

            await StartAutomationAsync();
        }

        private async Task StartAutomationAsync()
        {
            if (_automationRunning) return;

            _automationCts?.Cancel();
            _automationCts = new CancellationTokenSource();
            _automationPaused = false;
            _automationRunning = true;
            SetAutomationStatus("自动化中");

            try
            {
                await RunAutomationLoopAsync(_automationCts.Token);
            }
            catch (OperationCanceledException)
            {
                SetAutomationStatus("已停止");
            }
            catch (Exception ex)
            {
                PauseAutomationWithReason("出错：" + ex.Message);
            }
            finally
            {
                _automationRunning = false;
                AutoToggleButton.Text = "K";
            }
        }

        private async Task RunAutomationLoopAsync(CancellationToken ct)
        {
            int questionNo = 0;
            while (!ct.IsCancellationRequested)
            {
                await WaitIfPausedAsync(ct);
                questionNo++;
                SetAutomationStatus("自动化中");

                AutomationPageSnapshot snapshot;
                try
                {
                    snapshot = await GetAutomationSnapshotWithRetryAsync(ct);
                }
                catch (Exception ex)
                {
                    PauseAutomationWithReason("页面读取失败：" + ex.Message);
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(snapshot.QuestionText))
                {
                    PauseAutomationWithReason("未能定位当前内容：" + (string.IsNullOrWhiteSpace(snapshot.Diagnostic) ? "当前页面没有可用内容。" : snapshot.Diagnostic));
                    await WaitIfPausedAsync(ct);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(snapshot.Diagnostic))
                {
                    LogDebug("Automation snapshot: selector=" + snapshot.ContainerSelector + "; " + snapshot.Diagnostic);
                }

                if (string.IsNullOrWhiteSpace(snapshot.QuestionType) || snapshot.QuestionType.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    PauseAutomationWithReason("题型识别失败。");
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                if ((snapshot.QuestionType == "single" || snapshot.QuestionType == "multiple" || snapshot.QuestionType == "judge") &&
                    snapshot.Options.Count == 0)
                {
                    PauseAutomationWithReason("找不到选项。");
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                if ((snapshot.QuestionType == "blank" || snapshot.QuestionType == "essay") && snapshot.InputCount == 0)
                {
                    PauseAutomationWithReason("找不到输入框。");
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                string prompt = BuildAutomationPrompt(snapshot);
                QueryTextBox.Text = snapshot.QuestionText;
                OutputTextBox.Text = $"第 {questionNo} 项：正在生成内容...\n类型：{snapshot.QuestionType}";

                string answer = await SendApiRequestAsync(prompt);
                OutputTextBox.Text = answer;
                if (IsUncertainOutput(answer))
                {
                    PauseAutomationWithReason("内容为空或不确定。");
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                await WaitIfPausedAsync(ct);
                SetAutomationStatus("自动化中");
                LogDebug("Automation answer: type=" + snapshot.QuestionType
                         + "; answer=" + answer.Trim()
                         + "; options=" + string.Join(" | ", snapshot.Options.Select(o => $"{o.Key}:{o.Text}")));
                var apply = await ClientSession.ApplyAutomationTextAsync(answer, 9222, ct);
                if (!apply.Success)
                {
                    PauseAutomationWithReason(ToShortAutomationMessage(apply.Message));
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                await WaitIfPausedAsync(ct);
                int delayMs = GetAutomationDelayMs();
                SetAutomationStatus("自动化中");
                await DelayWithPauseAsync(delayMs, ct);

                await WaitIfPausedAsync(ct);
                SetAutomationStatus("自动化中");
                var next = await ClientSession.ClickAutomationNextAsync(9222, ct);
                LogDebug("Automation next: " + next.Message);
                if (!next.Success)
                {
                    PauseAutomationWithReason(ToShortAutomationMessage(next.Message));
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                bool changed = false;
                string afterSummary = "";
                for (int poll = 0; poll < 10; poll++)
                {
                    await Task.Delay(500, ct);
                    AutomationPageSnapshot afterNext;
                    try
                    {
                        afterNext = await GetAutomationSnapshotWithRetryAsync(ct);
                        afterSummary = SummarizeQuestion(afterNext.QuestionText);
                        if (!IsSameQuestion(snapshot.QuestionText, afterNext.QuestionText))
                        {
                            changed = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        afterSummary = "读取失败：" + ex.Message;
                    }
                }

                if (!changed)
                {
                    LogDebug("Automation no-progress: next=" + next.Message
                             + "; before=" + SummarizeQuestion(snapshot.QuestionText)
                             + "; after=" + afterSummary);
                    PauseAutomationWithReason("切换后内容未变化");
                    await WaitIfPausedAsync(ct);
                    continue;
                }
            }
        }

        private static string SummarizeQuestion(string text)
        {
            string s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 120 ? s[..120] : s;
        }

        private static async Task<AutomationPageSnapshot> GetAutomationSnapshotWithRetryAsync(CancellationToken ct)
        {
            Exception? last = null;
            for (int i = 0; i < 8; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await ClientSession.GetAutomationSnapshotAsync(9222, ct);
                }
                catch (Exception ex) when (IsTransientNavigationError(ex))
                {
                    last = ex;
                    await Task.Delay(350, ct);
                }
            }

            throw last ?? new InvalidOperationException("读取失败。");
        }

        private static bool IsTransientNavigationError(Exception ex)
        {
            string msg = ex.ToString();
            return msg.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("Inspected target navigated", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameQuestion(string a, string b)
        {
            string NormalizeQuestion(string s)
            {
                s = new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
                return s.Length > 300 ? s[..300] : s;
            }

            string x = NormalizeQuestion(a);
            string y = NormalizeQuestion(b);
            return x.Length >= 20 && string.Equals(x, y, StringComparison.Ordinal);
        }

        private string BuildAutomationPrompt(AutomationPageSnapshot snapshot)
        {
            var options = snapshot.Options.Count == 0
                ? ""
                : "\n选项：\n" + string.Join("\n", snapshot.Options.Select(o => $"{o.Key}. {o.Text}"));
            return $"""
请根据输入内容作答。必须只输出最终结果，不要解释。
类型：{snapshot.QuestionType}
规则：
- 单选/判断：只输出一个选项字母，如 A；判断题也优先输出页面选项字母。
- 多选：只输出多个选项字母，用逗号分隔，如 A,C。
- 填空/简答：只输出要填写到输入框中的正文。
- 如果无法确定，输出“不确定”。

内容：
{snapshot.QuestionText}
{options}
""";
        }

        private bool IsUncertainOutput(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return true;
            string s = answer.Trim();
            return s.Contains("不确定", StringComparison.OrdinalIgnoreCase)
                   || s.Contains("无法确定", StringComparison.OrdinalIgnoreCase)
                   || s.Contains("不能确定", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        }

        private int GetAutomationDelayMs()
        {
            double seconds = _config.AutoDelaySeconds;
            seconds = Math.Clamp(seconds, 0, 60);
            return (int)(seconds * 1000);
        }

        private async Task DelayWithPauseAsync(int delayMs, CancellationToken ct)
        {
            int elapsed = 0;
            while (elapsed < delayMs)
            {
                ct.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(ct);
                int step = Math.Min(100, delayMs - elapsed);
                await Task.Delay(step, ct);
                elapsed += step;
            }
        }

        private async Task WaitIfPausedAsync(CancellationToken ct)
        {
            while (_automationPaused)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(150, ct);
            }
        }

        private void PauseAutomationWithReason(string reason)
        {
            _automationPaused = true;
            SetAutomationStatus("已暂停");
            OutputTextBox.Text = "已暂停：" + ToShortAutomationMessage(reason);
            LogDebug("Automation paused: " + reason);
        }

        private void StopAutomation(string status)
        {
            _automationCts?.Cancel();
            _automationPaused = false;
            _automationRunning = false;
            SetAutomationStatus(status);
        }

        private void SetAutomationStatus(string status)
        {
            AutoToggleButton.Text = _automationRunning && !_automationPaused ? "G" : "K";
            AutoToggleButton.ToolTip = status;
            if (string.Equals(_lastAutomationStatus, status, StringComparison.Ordinal)) return;
            _lastAutomationStatus = status;
            if (status.StartsWith("已暂停", StringComparison.Ordinal) ||
                status.StartsWith("已停止", StringComparison.Ordinal) ||
                status.StartsWith("出错", StringComparison.Ordinal))
            {
                LogDebug("Automation: " + status);
            }
        }

        private static string ToShortAutomationMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "需要人工处理";
            string s = message.Replace("\r", " ").Replace("\n", " ").Trim();
            int cut = s.IndexOf('；');
            if (cut > 0) s = s[..cut];
            cut = s.IndexOf("; ", StringComparison.Ordinal);
            if (cut > 0) s = s[..cut];
            string[] noisy =
            {
                "selector=", "diag=", "diagnostic=", "candidates=", "raw=", "onclick=", "beforeUrl=", "afterUrl="
            };
            foreach (var marker in noisy)
            {
                cut = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (cut > 0) s = s[..cut].Trim();
            }
            return s.Length > 60 ? s[..60] + "..." : s;
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
                ExitAndDeleteSelfSilently();
            }
        }

        private void ToggleSettingsMode()
        {
            if (!_isSettingsMode)
            {
                _isSettingsMode = true;
                
                LoadConfigToInlineUI();
                
                MainToolbar.Visibility = Visibility.Collapsed;
                OutputScrollViewer.Visibility = Visibility.Collapsed;
                SettingsScrollViewer.Visibility = Visibility.Visible;
                
                this.Width = SettingsWindowWidth;
                this.Height = SettingsWindowHeight;
                MainContentGrid.Opacity = 1.0;
                ScheduleSettingsAutoClose(TimeSpan.FromSeconds(20));
            }
            else
            {
                SaveAndCloseSettings();
            }
        }

        private void CancelSettingsMode()
        {
            _isSettingsMode = false;
            
            _config = ConfigHelper.LoadConfig();
            _config.EnableAntiCapture = true;
            ApplyConfig();
            ApplyDisplayAffinity();
            
            SettingsScrollViewer.Visibility = Visibility.Collapsed;
            MainToolbar.Visibility = Visibility.Visible;
            OutputScrollViewer.Visibility = Visibility.Visible;
            
            this.Width = NormalWindowWidth;
            this.Height = NormalWindowHeight;
        }

        private void SetWindowWidthKeepingRightEdge(double width)
        {
            double right = this.Left + this.Width;
            this.Width = width;
            this.Left = right - this.Width;
        }

        private void LoadConfigToInlineUI()
        {
            _suppressOpacitySave = true;
            SettingsApiUrl.Text = _config.ApiUrl;
            SettingsApiKey.Password = _config.ApiKey;
            SettingsModelName.Text = _config.ModelName;
            InputOpacitySlider.Value = _config.TextOpacity;
            DotOpacitySlider.Value = _config.DotOpacity;
            _suppressOpacitySave = false;
        }

        private void SaveConfigFromInlineUI()
        {
            _config.ApiUrl = SettingsApiUrl.Text.Trim();
            _config.ApiKey = SettingsApiKey.Password.Trim();
            _config.ModelName = SettingsModelName.Text.Trim();
            _config.TextOpacity = InputOpacitySlider.Value;
            _config.DotOpacity = DotOpacitySlider.Value;
            _config.EnableAntiCapture = true;
            ConfigHelper.SaveConfig(_config);
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressOpacitySave) return;
            if (!_isSettingsMode) return;
            SaveConfigFromInlineUI();
            ApplyLiteOpacityConfig();
            ApplyDisplayAffinity();
            ScheduleSettingsAutoClose(TimeSpan.FromSeconds(20));
        }

        private void OpacitySlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSettingsMode) return;
            ScheduleSettingsAutoClose(TimeSpan.FromSeconds(20));
        }

        private void SettingsTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isSettingsMode || _suppressOpacitySave) return;
            ScheduleSettingsAutoClose(TimeSpan.FromSeconds(20));
        }

        private void SettingsPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_isSettingsMode || _suppressOpacitySave) return;
            ScheduleSettingsAutoClose(TimeSpan.FromSeconds(20));
        }

        private void SettingsSave_Click(object sender, RoutedEventArgs e)
        {
            SaveAndCloseSettings();
        }

        private void SettingsSurface_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isSettingsMode) return;
            ScheduleSettingsAutoClose(TimeSpan.FromSeconds(20));
            if (ReferenceEquals(e.OriginalSource, SettingsScrollViewer)) e.Handled = true;
        }

        private void ScheduleSettingsAutoClose(TimeSpan delay)
        {
            _settingsAutoCloseTimer?.Stop();
            _settingsAutoCloseTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = delay
            };
            _settingsAutoCloseTimer.Tick += (_, _) =>
            {
                _settingsAutoCloseTimer?.Stop();
                if (_isSettingsMode)
                {
                    if (SettingsScrollViewer.IsKeyboardFocusWithin || SettingsScrollViewer.IsMouseOver)
                    {
                        ScheduleSettingsAutoClose(TimeSpan.FromSeconds(20));
                    }
                    else
                    {
                        SaveAndCloseSettings();
                    }
                }
            };
            _settingsAutoCloseTimer.Start();
        }

        private void SaveAndCloseSettings()
        {
            _settingsAutoCloseTimer?.Stop();
            SaveConfigFromInlineUI();

            _isSettingsMode = false;
            ApplyConfig();
            ApplyDisplayAffinity();

            SettingsScrollViewer.Visibility = Visibility.Collapsed;
            MainToolbar.Visibility = Visibility.Visible;
            OutputScrollViewer.Visibility = Visibility.Visible;

            this.Width = NormalWindowWidth;
            this.Height = NormalWindowHeight;
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
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
            }
            catch
            {
                // Ignore
            }
        }

        public static void LogDebug(string message)
        {
#if DEBUG
            try
            {
                string logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                System.Diagnostics.Debug.WriteLine(logMsg);
            }
            catch
            {
                // Ignore
            }
#else
            _ = message;
            return;
#endif
        }
    }
}
