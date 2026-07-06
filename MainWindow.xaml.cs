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

namespace AI_desktop_tool
{
    public partial class MainWindow : Window
    {
        private const double NormalWindowWidth = 260;
        private const double SettingsWindowWidth = 360;

        private AppConfig _config = new AppConfig();
        private bool _isRequesting = false;
        private bool _isSettingsMode = false;
        private System.Windows.Threading.DispatcherTimer? _topmostTimer;
        private System.Windows.Threading.DispatcherTimer? _foregroundTrackerTimer;
        private System.Windows.Threading.DispatcherTimer? _keyboardHookWatchdogTimer;
        private IntPtr _lastExternalForeground = IntPtr.Zero;
        private IntPtr _lastExternalFocus = IntPtr.Zero;
        private GlobalKeyboardHook? _ctrlVHook;
        private bool _isHandlingSuppressedCtrlV = false;
        private CancellationTokenSource? _automationCts;
        private bool _automationPaused = false;
        private bool _automationRunning = false;
        private string _lastAutomationStatus = "";

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
            this.Width = NormalWindowWidth;
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            this.Left = screenWidth - this.Width - 50;
            this.Top = 80;

            // Initialize topmost timer to force overlay priority
            _topmostTimer = new System.Windows.Threading.DispatcherTimer();
            _topmostTimer.Interval = TimeSpan.FromMilliseconds(300);
            _topmostTimer.Tick += (s, ev) => ForceWindowTopmost();
            _topmostTimer.Start();

            _foregroundTrackerTimer = new System.Windows.Threading.DispatcherTimer();
            _foregroundTrackerTimer.Interval = TimeSpan.FromMilliseconds(100);
            _foregroundTrackerTimer.Tick += (s, ev) => TrackExternalForegroundWindow();
            _foregroundTrackerTimer.Start();

            _ctrlVHook = new GlobalKeyboardHook(() =>
                Dispatcher.InvokeAsync(HandleSuppressedCtrlVAsync).Task.Unwrap());
            _ctrlVHook.Start();
            GlobalKeyboardHook.RefreshSuppressionState();

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
            _keyboardHookWatchdogTimer?.Stop();
            _ctrlVHook?.Dispose();
            _ctrlVHook = null;
            base.OnClosed(e);
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
            LocalSearchButton.FontWeight = weight;
            CefCaptureButton.FontWeight = weight;
            AutoToggleButton.FontWeight = weight;
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
                GlobalKeyboardHook.RefreshSuppressionState();
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

        private async void LocalSearchButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            await TriggerLocalSearchAsync();
        }

        private async Task TriggerLocalSearchAsync()
        {
            if (_isRequesting) return;
            _isRequesting = true;
            LocalSearchButton.Text = "...";
            AnswerTextBlock.Text = "\u6b63\u5728\u8bfb\u53d6\u5f53\u524d\u9898\u76ee\u5e76\u68c0\u7d22\u8fd1\u4ee3\u53f2\u9898\u5e93...";

            try
            {
                string query;
                string questionType = "";
                List<LocalSearchOption> currentOptions = new();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    var snapshot = await CefDomExtractor.GetAutomationSnapshotAsync(9222, cts.Token);
                    query = snapshot.QuestionText?.Trim() ?? "";
                    questionType = snapshot.QuestionType?.Trim() ?? "";
                    currentOptions = snapshot.Options
                        .Where(o => !string.IsNullOrWhiteSpace(o.Key) && !string.IsNullOrWhiteSpace(o.Text))
                        .Select(o => new LocalSearchOption(o.Key, o.Text))
                        .ToList();
                }
                catch
                {
                    query = await CefDomExtractor.ExtractBestTextAsync(9222, cts.Token);
                }

                QueryTextBox.Text = query;
                QueryTextBox.CaretIndex = QueryTextBox.Text.Length;

                if (string.IsNullOrWhiteSpace(query)
                    || query.StartsWith("\u672a\u53d1\u73b0\u53ef\u8bfb\u53d6", StringComparison.Ordinal)
                    || query.StartsWith("\u5df2\u8fde\u63a5 CEF", StringComparison.Ordinal))
                {
                    AnswerTextBlock.Text = "\u672a\u80fd\u8bfb\u53d6\u5f53\u524d\u9898\u9762\uff0c\u65e0\u6cd5\u68c0\u7d22\u672c\u5730\u9898\u5e93\u3002";
                    return;
                }

                var match = await LocalQuestionBank.SearchModernHistoryAsync(query, questionType, currentOptions);
                if (match == null)
                {
                    AnswerTextBlock.Text = "\u8fd1\u4ee3\u53f2\u9898\u5e93\u672a\u547d\u4e2d\u3002";
                    return;
                }

                AnswerTextBlock.Text = match.Answer;
                LogDebug($"Local bank hit: id={match.Id}; type={match.Type}; score={match.Similarity:0.000}; prompt={match.Prompt}");
            }
            catch (Exception ex)
            {
                AnswerTextBlock.Text = $"\u672c\u5730\u9898\u5e93\u68c0\u7d22\u5931\u8d25: {ex.Message}";
            }
            finally
            {
                LocalSearchButton.Text = "s";
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
                    text = AnswerTextBlock.Text;
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
                await KeyboardSimulator.SendStringAsync(text, 0);
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
            return (Win32Helper.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                   || (Win32Helper.GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0
                   || (Win32Helper.GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
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
                    PauseAutomationWithReason("题面识别失败/CDP 连接失败：" + ex.Message);
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(snapshot.QuestionText))
                {
                    PauseAutomationWithReason("未能定位当前题面：" + (string.IsNullOrWhiteSpace(snapshot.Diagnostic) ? "当前页面没有可用题面。" : snapshot.Diagnostic));
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
                AnswerTextBlock.Text = $"第 {questionNo} 题：正在生成答案...\n题型：{snapshot.QuestionType}";

                string answer = await SendApiRequestAsync(prompt);
                AnswerTextBlock.Text = answer;
                if (IsUncertainAnswer(answer))
                {
                    PauseAutomationWithReason("AI 答案为空或不确定。");
                    await WaitIfPausedAsync(ct);
                    continue;
                }

                await WaitIfPausedAsync(ct);
                SetAutomationStatus("自动化中");
                LogDebug("Automation answer: type=" + snapshot.QuestionType
                         + "; answer=" + answer.Trim()
                         + "; options=" + string.Join(" | ", snapshot.Options.Select(o => $"{o.Key}:{o.Text}")));
                var apply = await CefDomExtractor.ApplyAutomationAnswerAsync(answer, 9222, ct);
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
                var next = await CefDomExtractor.ClickAutomationNextAsync(9222, ct);
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
                    PauseAutomationWithReason("下一题后题面未变化");
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
                    return await CefDomExtractor.GetAutomationSnapshotAsync(9222, ct);
                }
                catch (Exception ex) when (IsTransientNavigationError(ex))
                {
                    last = ex;
                    await Task.Delay(350, ct);
                }
            }

            throw last ?? new InvalidOperationException("读取题面失败。");
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
请回答考试题。必须只输出最终答案，不要解释。
题型：{snapshot.QuestionType}
规则：
- 单选/判断：只输出一个选项字母，如 A；判断题也优先输出页面选项字母。
- 多选：只输出多个选项字母，用逗号分隔，如 A,C。
- 填空/简答：只输出要填写到输入框中的答案正文。
- 如果无法确定，输出“不确定”。

题面：
{snapshot.QuestionText}
{options}
""";
        }

        private bool IsUncertainAnswer(string answer)
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
            AnswerTextBlock.Text = "已暂停：" + ToShortAutomationMessage(reason);
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
                
                SetWindowWidthKeepingRightEdge(SettingsWindowWidth);
                this.Height = 380;
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
                
                SetWindowWidthKeepingRightEdge(NormalWindowWidth);
                this.Height = 170;
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
            
            SetWindowWidthKeepingRightEdge(NormalWindowWidth);
            this.Height = 170;
        }

        private void SetWindowWidthKeepingRightEdge(double width)
        {
            double right = this.Left + this.Width;
            this.Width = width;
            this.Left = right - this.Width;
        }

        private void LoadConfigToInlineUI()
        {
            SetApiUrlTextBox.Text = _config.ApiUrl;
            SetApiKeyTextBox.Text = _config.ApiKey;
            SetModelTextBox.Text = _config.ModelName;
            SetPromptTextBox.Text = _config.SystemPrompt;
            
            OpacitySlider.Value = _config.TextOpacity;
            DelaySlider.Value = _config.TypeDelayMs;
            AutoDelaySlider.Value = _config.AutoDelaySeconds <= 0 ? 2.0 : _config.AutoDelaySeconds;
            AntiCaptureCheckBox.IsChecked = _config.EnableAntiCapture;

            string currentWeight = _config.FontWeight ?? "Normal";
            bool foundWeight = false;
            foreach (ComboBoxItem item in FontWeightComboBox.Items)
            {
                if (string.Equals(item.Content?.ToString(), currentWeight, StringComparison.OrdinalIgnoreCase))
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
            _config.AutoDelaySeconds = AutoDelaySlider.Value;
            _config.EnableAntiCapture = AntiCaptureCheckBox.IsChecked == true;

            if (FontWeightComboBox.SelectedItem is ComboBoxItem selectedWeightItem)
            {
                _config.FontWeight = selectedWeightItem.Content?.ToString() ?? "Normal";
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
