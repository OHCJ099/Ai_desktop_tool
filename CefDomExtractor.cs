using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsClient
{
    /// <summary>
    /// CEF/Chromium DevTools Protocol text extractor.
    ///
    /// This bypasses normal screenshot and Windows UIA limitations by reading the
    /// actual Chromium page runtime. The target CEF app must be launched with:
    ///   --remote-debugging-port=9222 --remote-allow-origins=*
    /// </summary>
    public static class ClientSession
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static int _seq;

        public static Task<bool> IsChannelAvailableAsync(int port = 9222, CancellationToken ct = default)
        {
            return IsChannelAliveAsync(port, ct);
        }

        public static async Task<string> ExtractBestTextAsync(int port = 9222, CancellationToken ct = default)
        {
            await EnsureChannelAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0)
            {
                // Some CEF builds expose no /json/list entries until a web page is opened.
                // Try browser-level Target.getTargets as a fallback.
                targets = await GetTargetsViaHostAsync(port, ct);
            }

            var candidates = targets
                .Where(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl) &&
                            (t.Type.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("webview", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("iframe", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var parts = new List<string>();
            foreach (var t in candidates)
            {
                try
                {
                    var text = await ExtractFromPageWebSocketAsync(t.WebSocketDebuggerUrl!, ct);
                    text = Normalize(text);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Keep only the extracted question text. Do not prepend title/URL:
                        // long exam URLs waste prompt tokens and confuse the LLM.
                        parts.Add(text);
                    }
                }
                catch
                {
                    // Keep probing other targets.
                }
            }

            var merged = Normalize(string.Join("\n\n---\n\n", parts));
            if (!string.IsNullOrWhiteSpace(merged)) return merged;

            if (targets.Count == 0)
            {
                return "未发现可读取页面，请确认目标页面已打开后重试。";
            }

            return "已连接，但当前没有可读取页面；请先进入目标页面后再试。";
        }

        public static async Task<bool> TryApplyTextAsync(string answer, int port = 9222, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(answer)) return false;

            await EnsureChannelAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaHostAsync(port, ct);

            var candidates = targets
                .Where(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl) &&
                            (t.Type.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("webview", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("iframe", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var t in candidates)
            {
                try
                {
                    if (await ApplyTextIntoPageWebSocketAsync(t.WebSocketDebuggerUrl!, answer, ct))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Try next target.
                }
            }

            return false;
        }

        public static async Task<bool> EnablePasteAsync(int port = 9222, CancellationToken ct = default)
        {
            await EnsureChannelAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaHostAsync(port, ct);

            var candidates = targets
                .Where(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl) &&
                            (t.Type.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("webview", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("iframe", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            bool ok = false;
            foreach (var t in candidates)
            {
                try
                {
                    ok |= await EnablePasteInPageWebSocketAsync(t.WebSocketDebuggerUrl!, ct);
                }
                catch
                {
                    // Try next target.
                }
            }

            return ok;
        }

        public static async Task<AutomationPageSnapshot> GetAutomationSnapshotAsync(int port = 9222, CancellationToken ct = default)
        {
            await EnsureChannelAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaHostAsync(port, ct);

            var candidates = targets
                .Where(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl) &&
                            (t.Type.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("webview", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("iframe", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("未发现可读取页面。");
            }

            Exception? lastError = null;
            foreach (var t in candidates)
            {
                try
                {
                    var snapshot = await GetAutomationSnapshotFromPageAsync(t.WebSocketDebuggerUrl!, ct);
                    if (!string.IsNullOrWhiteSpace(snapshot.QuestionText))
                    {
                        snapshot.TargetTitle = t.Title;
                        snapshot.TargetUrl = t.Url;
                        return snapshot;
                    }
                    if (!string.IsNullOrWhiteSpace(snapshot.Diagnostic))
                    {
                        lastError = new InvalidOperationException(snapshot.Diagnostic);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(lastError?.Message ?? "未能定位当前内容。");
        }

        public static async Task<AutomationActionResult> ApplyAutomationTextAsync(string answer, int port = 9222, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(answer))
            {
                return new AutomationActionResult(false, "内容为空。");
            }

            await EnsureChannelAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaHostAsync(port, ct);

            var candidates = targets
                .Where(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl) &&
                            (t.Type.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("webview", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("iframe", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (candidates.Count == 0)
            {
                return new AutomationActionResult(false, "页面不存在。");
            }

            string last = "页面操作失败。";
            foreach (var t in candidates)
            {
                try
                {
                    var result = await ApplyAutomationTextInPageAsync(t.WebSocketDebuggerUrl!, answer, ct);
                    if (result.Success) return result;
                    last = result.Message;
                }
                catch (Exception ex)
                {
                    last = ex.Message;
                }
            }

            return new AutomationActionResult(false, last);
        }

        public static async Task<AutomationActionResult> ClickAutomationNextAsync(int port = 9222, CancellationToken ct = default)
        {
            await EnsureChannelAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaHostAsync(port, ct);

            var candidates = targets
                .Where(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl) &&
                            (t.Type.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("webview", StringComparison.OrdinalIgnoreCase) ||
                             t.Type.Equals("iframe", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (candidates.Count == 0)
            {
                return new AutomationActionResult(false, "页面不存在。");
            }

            string last = "找不到下一题按钮。";
            foreach (var t in candidates)
            {
                try
                {
                    var result = await ClickAutomationNextInPageAsync(t.WebSocketDebuggerUrl!, ct);
                    if (result.Success) return result;
                    last = result.Message;
                }
                catch (Exception ex)
                {
                    last = ex.Message;
                }
            }

            return new AutomationActionResult(false, last);
        }

        public static async Task<string> DumpPageDiagnosticsAsync(int port = 9222, CancellationToken ct = default)
        {
            await EnsureChannelAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaHostAsync(port, ct);

            var sb = new StringBuilder();
            sb.AppendLine("page diagnostics");
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            foreach (var t in targets.Where(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl)))
            {
                sb.AppendLine("==== TARGET ====");
                sb.AppendLine($"type={t.Type}");
                sb.AppendLine($"title={t.Title}");
                sb.AppendLine($"url={t.Url}");
                sb.AppendLine();

                try
                {
                    sb.AppendLine(await DiagnosePageWebSocketAsync(t.WebSocketDebuggerUrl!, ct));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("DIAG_ERROR: " + ex.Message);
                }
                sb.AppendLine();
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "page_diag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            string appPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "page_diag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");

            string content = sb.ToString();
            string written = "";
            try
            {
                File.WriteAllText(path, content, Encoding.UTF8);
                written = path;
            }
            catch { }

            try
            {
                File.WriteAllText(appPath, content, Encoding.UTF8);
                written = string.IsNullOrWhiteSpace(written) ? appPath : written + "\n" + appPath;
            }
            catch { }

            if (string.IsNullOrWhiteSpace(written))
            {
                throw new IOException("诊断文件写入失败。");
            }

            return written;
        }

        private static async Task EnsureChannelAvailableAsync(int port, CancellationToken ct)
        {
            // Only probe the channel endpoint. Do not auto-start CXExam from this helper;
            // the channel desktop shortcut is created separately and the user opens it manually.
            await IsChannelAliveAsync(port, ct);
        }

        private static async Task<bool> IsChannelAliveAsync(int port, CancellationToken ct)
        {
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
                return json.Contains("webSocketDebuggerUrl", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<List<PageTarget>> GetTargetsAsync(int port, CancellationToken ct)
        {
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
                return JsonSerializer.Deserialize<List<PageTarget>>(json, JsonOpts()) ?? new List<PageTarget>();
            }
            catch
            {
                return new List<PageTarget>();
            }
        }

        private static async Task<List<PageTarget>> GetTargetsViaHostAsync(int port, CancellationToken ct)
        {
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
                using var doc = JsonDocument.Parse(json);
                string? browserWs = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                if (string.IsNullOrWhiteSpace(browserWs)) return new List<PageTarget>();

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(browserWs), ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                var resp = await SendChannelAsync(ws, "Target.getTargets", null, timeout.Token);
                var list = new List<PageTarget>();
                if (resp.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("targetInfos", out var infos))
                {
                    foreach (var info in infos.EnumerateArray())
                    {
                        string targetId = GetString(info, "targetId") ?? "";
                        list.Add(new PageTarget
                        {
                            Id = targetId,
                            Type = GetString(info, "type") ?? "",
                            Title = GetString(info, "title") ?? "",
                            Url = GetString(info, "url") ?? "",
                            // Target.getTargets does not include webSocketDebuggerUrl. Chromium's
                            // page endpoint is nevertheless deterministic from the browser endpoint
                            // and target id, so expose a connectable target to the normal pipeline.
                            WebSocketDebuggerUrl = BuildPageWebSocketUrl(browserWs, targetId)
                        });
                    }
                }
                return list;
            }
            catch
            {
                return new List<PageTarget>();
            }
        }

        private static string? BuildPageWebSocketUrl(string browserWs, string targetId)
        {
            if (string.IsNullOrWhiteSpace(browserWs) || string.IsNullOrWhiteSpace(targetId)) return null;
            try
            {
                var browserUri = new Uri(browserWs);
                return browserUri.GetLeftPart(UriPartial.Authority)
                       + "/devtools/page/"
                       + Uri.EscapeDataString(targetId);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> ExtractFromPageWebSocketAsync(string wsUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            await SendChannelAsync(ws, "Runtime.enable", null, timeout.Token);

            // DOM-level extraction:
            // 1) current browser selection, if any;
            // 2) smallest visible question-like containers;
            // 3) visible leaf texts with a hard length cap.
            // This avoids sending the whole page to the LLM.
            const string expression = """
(() => {
  const MAX = 4200;
  const seen = new Set();
  const out = [];
  const badRe = /(\u4e0a\u4e00\u9898|\u4e0b\u4e00\u9898|\u4ea4\u5377|\u63d0\u4ea4|\u8fd4\u56de|\u9000\u51fa|\u5012\u8ba1\u65f6|\u8003\u8bd5\u987b\u77e5|\u4fdd\u5b58|\u6807\u8bb0|\u672a\u7b54|\u5df2\u7b54|\u7b54\u9898\u5361|\u9898\u53f7|\u767b\u5f55|\u5237\u65b0|\u5173\u95ed|\u5168\u5c4f|\u6536\u8d77|\u5c55\u5f00|\u5f53\u524d\u9898\u76ee|\u5df2\u4f5c\u7b54|\u672a\u4f5c\u7b54|remainTime|monitorStatus)/;
  const toolbarRe = /^(段落|arial|sans-serif|serif|微软雅黑|宋体|黑体|(?:1[0-9]|2[0-9]|[8-9])px)$/i;
  const qRe = /(\u5355\u9009|\u591a\u9009|\u5224\u65ad|\u586b\u7a7a|\u7b80\u7b54|\u8bba\u8ff0|\u6750\u6599|\u9605\u8bfb|\u9898|\u95ee\u9898|\u8bf7\u9009\u62e9|\u6b63\u786e\u7684\u662f|\u9519\u8bef\u7684\u662f|\u4e0b\u5217|\u7b54\u6848|A[\.．、]|B[\.．、]|C[\.．、]|D[\.．、]|①|②|③|④)/i;
  function norm(s) {
    return String(s || '')
      .replace(/\r/g, '\n')
      .split('\n')
      .map(x => x.replace(/[ \t\f\v]+/g, ' ').trim())
      .filter(x => x && !toolbarRe.test(x))
      .join('\n')
      .replace(/\n{3,}/g, '\n\n')
      .trim();
  }
  function lineCount(s) { return norm(s).split('\n').filter(Boolean).length; }
  function standaloneNumberCount(s) { return (norm(s).match(/^\d{1,3}$/gm) || []).length; }
  function navLike(s) {
    s = norm(s);
    const nums = standaloneNumberCount(s);
    return nums >= 8 && nums >= lineCount(s) * 0.45;
  }
  function visible(el) {
    if (!el || el.nodeType !== 1) return false;
    const st = getComputedStyle(el);
    if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
  }
  function push(s) {
    s = norm(s);
    if (!s || s.length < 2 || badRe.test(s) || navLike(s)) return;
    if (seen.has(s)) return;
    seen.add(s); out.push(s);
  }
  function currentQuestionNumber() {
    try {
      const u = new URL(location.href);
      const start = u.searchParams.get('start');
      if (start && /^\d+$/.test(start)) return String(Number(start) + 1);
    } catch {}
    return null;
  }
  function cleanQuestionText(s, qn) {
    s = norm(s);
    if (!s) return '';
    // Drop URL/title and repeated navigation chunks before the actual current question.
    if (qn) {
      const re = new RegExp('(?:^|\\n)\\s*' + qn.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*[\\.．、]?\\s*(?:\\n|\\(|（|$)');
      const m = re.exec(s);
      if (m && m.index > 0) s = s.slice(m.index).trim();
    }
    const rawLines = s.split('\n').map(x => x.trim()).filter(Boolean);
    const kept = [];
    for (let i = 0; i < rawLines.length; i++) {
      let ln = rawLines[i];
      if (toolbarRe.test(ln)) continue;
      if (/^\d{1,3}$/.test(ln)) continue;
      if (/^[一二三四五六七八九十]+、\s*(单选题|多选题|填空题|判断题|简答题)/.test(ln) && kept.length > 0) continue;
      if (badRe.test(ln)) continue;
      // Compress option letter + option text:
      // A \n xxx  => A. xxx
      if (/^[A-H]$/.test(ln) && i + 1 < rawLines.length) {
        const next = rawLines[i + 1];
        if (next && !/^[A-H]$/.test(next) && !toolbarRe.test(next) && !badRe.test(next)) {
          kept.push(ln + '. ' + next);
          i++;
          continue;
        }
      }
      kept.push(ln);
    }
    return kept.join('\n').slice(0, MAX).trim();
  }
  function scoreText(s) {
    s = norm(s);
    let score = 0;
    if (qRe.test(s)) score += 30;
    score += Math.min(s.length / 20, 30);
    const opts = (s.match(/[A-D][\.．、]/gi) || []).length;
    score += opts * 12;
    const nums = standaloneNumberCount(s);
    if (nums >= 5) score -= nums * 8;
    if (navLike(s)) score -= 120;
    if (s.length > 3000) score -= 35;
    if (badRe.test(s)) score -= 50;
    return score;
  }

  const selected = norm(String(getSelection && getSelection()));
  if (selected.length >= 4) return selected.slice(0, MAX);

  const qn = currentQuestionNumber();
  if (qn) {
    const qnRe = new RegExp('(?:^|\\n)\\s*' + qn.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*[\\.．、]?\\s*(?:\\n|\\(|（|$)');
    const exact = [];
    for (const el of document.querySelectorAll('div, section, article, main, li, table, tbody, form, [class*="question"], [class*="ques"], [class*="TiMu"], [class*="timu"], [id*="question"], [id*="ques"]')) {
      if (!visible(el)) continue;
      const text = norm(el.innerText || el.textContent || '');
      if (text.length < 10 || text.length > 8000) continue;
      if (!qnRe.test(text)) continue;
      if (navLike(text) && !/(简答题|单选题|多选题|判断题|填空题|[A-D][\.．、])/.test(text)) continue;
      const cleaned = cleanQuestionText(text, qn);
      if (cleaned.length >= 8) {
        const r = el.getBoundingClientRect();
        const area = Math.max(1, r.width * r.height);
        exact.push({ text: cleaned, score: scoreText(cleaned) - Math.log(area) / 5 });
      }
    }
    exact.sort((a, b) => b.score - a.score || a.text.length - b.text.length);
    if (exact.length) return exact[0].text.slice(0, MAX);
  }

  const candidates = [];
  for (const el of document.querySelectorAll('article, section, main, [class*="question"], [class*="ques"], [id*="question"], [id*="ques"], .TiMu, .timu, .stem, .subject, div, li, p, table')) {
    if (!visible(el)) continue;
    const text = norm(el.innerText || el.textContent || '');
    if (text.length < 8 || text.length > 6000) continue;
    const childTextLen = Array.from(el.children || []).reduce((n, c) => n + norm(c.innerText || c.textContent || '').length, 0);
    // Prefer compact containers instead of the entire page body.
    if (el !== document.body && childTextLen > 0 && text.length > childTextLen * 1.35 && text.length > 1200) continue;
    const r = el.getBoundingClientRect();
    const centerPenalty = Math.abs((r.top + r.bottom) / 2 - innerHeight / 2) / Math.max(innerHeight, 1) * 10;
    const score = scoreText(text) - centerPenalty;
    if (score > 15) candidates.push({ score, text });
  }
  candidates.sort((a, b) => b.score - a.score || a.text.length - b.text.length);
  if (candidates.length) return cleanQuestionText(candidates.slice(0, 2).map(x => x.text).join('\n\n---\n\n'), qn).slice(0, MAX);

  for (const el of document.querySelectorAll('input, textarea, [aria-label], [title], img[alt], option, button, p, li, td, th, span, label')) {
    if (!visible(el)) continue;
    push(el.value);
    push(el.getAttribute('aria-label'));
    push(el.getAttribute('title'));
    push(el.getAttribute('alt'));
    push(el.innerText || el.textContent);
    if (out.join('\n').length > MAX) break;
  }
  return out.join('\n').slice(0, MAX);
})()
""";

            var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
            }, timeout.Token);

            string domText = ExtractRuntimeString(eval);

            // Accessibility tree from Chromium itself. This is not Windows UIA.
            // Use it only as a fallback; otherwise it may reintroduce full-page noise.
            string axText = "";
            if (string.IsNullOrWhiteSpace(domText))
            {
                try
                {
                    var ax = await SendChannelAsync(ws, "Accessibility.getFullAXTree", null, timeout.Token);
                    axText = ExtractAxNames(ax);
                }
                catch { }
            }

            return Normalize(domText + "\n" + axText);
        }

        private static async Task<bool> ApplyTextIntoPageWebSocketAsync(string wsUrl, string answer, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            await SendChannelAsync(ws, "Runtime.enable", null, timeout.Token);

            string answerJson = JsonSerializer.Serialize(answer);
            string expression = $$$"""
(() => {
  const ANSWER = {{{answerJson}}};

  function currentQuestionLooksTextual() {
    const text = (document.body && document.body.innerText || '').replace(/\s+/g, ' ');
    return /(简答题|填空题|论述题|问答题|主观题)/.test(text);
  }
  if (!currentQuestionLooksTextual()) return false;

  function htmlEscape(s) {
    return String(s || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }
  function answerHtml(s) {
    return String(s || '')
      .replace(/\r\n/g, '\n')
      .replace(/\r/g, '\n')
      .split('\n')
      .map(x => '<p>' + (htmlEscape(x) || '<br/>') + '</p>')
      .join('');
  }
  function fire(el) {
    if (!el) return;
    for (const name of ['input', 'change', 'keyup', 'blur']) {
      try { el.dispatchEvent(new Event(name, { bubbles: true, cancelable: true })); } catch {}
    }
  }

  // Chaoxing subjective questions use UEditor:
  //   iframe#ueditor_0 -> body.view[contenteditable=true]
  //   hidden textarea#answer{questionId}
  // Prefer its public API, then hard-sync iframe body + hidden textarea.
  function applyEditor() {
    let ok = false;
    const html = answerHtml(ANSWER);
    try {
      if (window.UE && UE.instants) {
        for (const key of Object.keys(UE.instants)) {
          const inst = UE.instants[key];
          if (!inst) continue;
          try {
            if (typeof inst.setContent === 'function') {
              inst.setContent(html);
              ok = true;
            } else if (inst.body) {
              inst.body.innerHTML = html;
              ok = true;
            }
            if (typeof inst.fireEvent === 'function') {
              try { inst.fireEvent('contentchange'); } catch {}
              try { inst.fireEvent('selectionchange'); } catch {}
            }
          } catch {}
        }
      }
    } catch {}

    try {
      const iframe = document.getElementById('ueditor_0') || document.querySelector('iframe[id^="ueditor_"]');
      const d = iframe && (iframe.contentDocument || iframe.contentWindow.document);
      const body = d && d.body;
      if (body) {
        body.focus();
        body.innerHTML = html;
        fire(body);
        ok = true;
      }
    } catch {}

    try {
      const qid = (document.getElementById('questionId') && document.getElementById('questionId').value)
               || (document.getElementById('jiandaId') && document.getElementById('jiandaId').value)
               || '';
      const ta = (qid && document.getElementById('answer' + qid))
              || document.querySelector('textarea[id^="answer"],textarea[name^="answer"]');
      if (ta) {
        ta.value = html;
        ta.textContent = html;
        fire(ta);
        ok = true;
      }
    } catch {}

    return ok;
  }
  if (applyEditor()) return true;

  function visible(el) {
    if (!el || el.nodeType !== 1) return false;
    const st = getComputedStyle(el);
    if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 10 && r.height > 10 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
  }

  function editable(el) {
    if (!el || el.nodeType !== 1) return false;
    const tag = el.tagName && el.tagName.toLowerCase();
    if (tag === 'textarea') return !el.disabled && !el.readOnly;
    if (tag === 'input') {
      const type = (el.type || 'text').toLowerCase();
      return ['text', 'search', 'email', 'url', 'tel', 'password', 'number'].includes(type) && !el.disabled && !el.readOnly;
    }
    if (el.isContentEditable) return true;
    return false;
  }

  function fire(el) {
    for (const name of ['input', 'change', 'keyup', 'blur']) {
      try { el.dispatchEvent(new Event(name, { bubbles: true, cancelable: true })); } catch {}
    }
  }

  function setNativeValue(el, value) {
    const tag = el.tagName && el.tagName.toLowerCase();
    if (tag === 'textarea' || tag === 'input') {
      const proto = tag === 'textarea' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      const desc = Object.getOwnPropertyDescriptor(proto, 'value');
      if (desc && desc.set) desc.set.call(el, value);
      else el.value = value;
      fire(el);
      return true;
    }

    if (el.isContentEditable) {
      el.focus();
      try {
        const doc = el.ownerDocument || document;
        doc.execCommand('selectAll', false, null);
        doc.execCommand('insertText', false, value);
      } catch {
        el.innerText = value;
      }
      fire(el);
      return true;
    }

    return false;
  }

  function score(el) {
    if (!visible(el) || !editable(el)) return -9999;
    const r = el.getBoundingClientRect();
    let s = 0;
    const hint = ((el.id || '') + ' ' + (el.className || '') + ' ' + (el.name || '') + ' ' + (el.placeholder || '') + ' ' + (el.getAttribute('aria-label') || '')).toLowerCase();
    if (/answer|daan|reply|editor|ueditor|content|textarea|subject|write|\u586b|\u7b54|\u7b54\u6848|\u4f5c\u7b54|\u56de\u7b54/.test(hint)) s += 80;
    if (document.activeElement === el) s += 120;
    if (el.isContentEditable) s += 35;
    if ((el.tagName || '').toLowerCase() === 'textarea') s += 50;
    s += Math.min(r.width * r.height / 2000, 80);
    // Answer boxes are usually in the lower/middle content area, not the left navigation.
    if (r.left < innerWidth * 0.18) s -= 60;
    if (r.top < innerHeight * 0.18) s -= 20;
    return s;
  }

  function collect(doc) {
    const list = [];
    const active = doc.activeElement;
    if (editable(active)) list.push(active);
    for (const sel of [
      'textarea',
      'input[type=text]',
      '[contenteditable=true]',
      '[contenteditable=""]',
      '.ql-editor',
      '.tox-edit-area iframe',
      '.edui-editor-iframe',
      'iframe'
    ]) {
      for (const el of doc.querySelectorAll(sel)) list.push(el);
    }
    return list;
  }

  function tryDoc(doc) {
    const candidates = [];
    for (const el of collect(doc)) {
      if ((el.tagName || '').toLowerCase() === 'iframe') {
        try {
          const idoc = el.contentDocument || el.contentWindow.document;
          if (idoc) {
            const inner = tryDoc(idoc);
            if (inner) return true;
          }
        } catch {}
        continue;
      }
      candidates.push({ el, score: score(el) });
    }
    candidates.sort((a, b) => b.score - a.score);
    for (const c of candidates) {
      if (c.score < 0) continue;
      try {
        c.el.scrollIntoView({ block: 'center', inline: 'nearest' });
        c.el.focus();
        if (setNativeValue(c.el, ANSWER)) return true;
      } catch {}
    }
    return false;
  }

  return tryDoc(document);
})()
""";

            var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true,
                ["userGesture"] = true
            }, timeout.Token);

            try
            {
                if (eval.RootElement
                    .GetProperty("result")
                    .GetProperty("result")
                    .GetProperty("value")
                    .GetBoolean())
                {
                    if (await VerifyTextPresentAsync(ws, answer, timeout.Token))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            // Last channel-level attempt: focus the best editable element, select all, then use
            // Input.insertText. This goes through Chromium's input pipeline and often works
            // when direct DOM value assignment is ignored by custom editors.
            string focusExpression = """
(() => {
  const pageText = (document.body && document.body.innerText || '').replace(/\s+/g, ' ');
  if (!/(简答题|填空题|论述题|问答题|主观题)/.test(pageText)) return { ok: false };

  function visible(el) {
    if (!el || el.nodeType !== 1) return false;
    const st = getComputedStyle(el);
    if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 10 && r.height > 10 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
  }
  function editable(el) {
    if (!el || el.nodeType !== 1) return false;
    const tag = (el.tagName || '').toLowerCase();
    if (tag === 'textarea') return !el.disabled && !el.readOnly;
    if (tag === 'input') return ['text','search','email','url','tel','password','number'].includes((el.type || 'text').toLowerCase()) && !el.disabled && !el.readOnly;
    return !!el.isContentEditable;
  }
  function score(el) {
    if (!visible(el) || !editable(el)) return -9999;
    const r = el.getBoundingClientRect();
    const hint = ((el.id||'')+' '+(el.className||'')+' '+(el.name||'')+' '+(el.placeholder||'')+' '+(el.getAttribute('aria-label')||'')).toLowerCase();
    let s = r.width * r.height / 1000;
    if (/answer|daan|reply|editor|ueditor|content|textarea|subject|write|\u586b|\u7b54|\u7b54\u6848|\u4f5c\u7b54|\u56de\u7b54/.test(hint)) s += 100;
    if (document.activeElement === el) s += 150;
    if (el.isContentEditable) s += 50;
    if (r.left < innerWidth * 0.18) s -= 80;
    return s;
  }
  const arr = [];
  for (const sel of ['textarea','input[type=text]','[contenteditable=true]','[contenteditable=""]','.ql-editor','body[contenteditable=true]']) {
    for (const el of document.querySelectorAll(sel)) arr.push(el);
  }
  arr.sort((a,b) => score(b)-score(a));
  for (const el of arr) {
    if (score(el) < 0) continue;
    try {
      el.scrollIntoView({block:'center', inline:'nearest'});
      el.focus();
      try { document.execCommand('selectAll', false, null); } catch {}
      const r = el.getBoundingClientRect();
      return { ok: true, x: r.left + r.width / 2, y: r.top + r.height / 2 };
    } catch {}
  }
  return { ok: false };
})()
""";

            var focusEval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = focusExpression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true,
                ["userGesture"] = true
            }, timeout.Token);

            bool focused = false;
            double x = 0, y = 0;
            try
            {
                var val = focusEval.RootElement.GetProperty("result").GetProperty("result").GetProperty("value");
                focused = val.GetProperty("ok").GetBoolean();
                if (focused)
                {
                    x = val.GetProperty("x").GetDouble();
                    y = val.GetProperty("y").GetDouble();
                }
            }
            catch { }

            if (focused)
            {
                await SendChannelAsync(ws, "Input.dispatchMouseEvent", new Dictionary<string, object?>
                {
                    ["type"] = "mousePressed",
                    ["x"] = x,
                    ["y"] = y,
                    ["button"] = "left",
                    ["clickCount"] = 1
                }, timeout.Token);
                await SendChannelAsync(ws, "Input.dispatchMouseEvent", new Dictionary<string, object?>
                {
                    ["type"] = "mouseReleased",
                    ["x"] = x,
                    ["y"] = y,
                    ["button"] = "left",
                    ["clickCount"] = 1
                }, timeout.Token);
                await SendChannelAsync(ws, "Input.insertText", new Dictionary<string, object?>
                {
                    ["text"] = answer
                }, timeout.Token);
                return await VerifyTextPresentAsync(ws, answer, timeout.Token);
            }

            return false;
        }

        private static async Task<bool> EnablePasteInPageWebSocketAsync(string wsUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            await SendChannelAsync(ws, "Runtime.enable", null, timeout.Token);
            try { await SendChannelAsync(ws, "Page.enable", null, timeout.Token); } catch { }

            const string pasteUnlockScript = """
(() => {
  const MARK = '__win_client_input_20260701__';

  function isPasteLikeEvent(e) {
    try {
      const t = String(e && e.type || '').toLowerCase();
      if (t === 'paste') return true;
      if ((t === 'keydown' || t === 'keypress' || t === 'keyup') &&
          (e.ctrlKey || e.metaKey) && String(e.key || '').toLowerCase() === 'v') return true;
      if (t === 'beforeinput' && /paste/i.test(String(e.inputType || ''))) return true;
    } catch {}
    return false;
  }

  function unlockWindow(win) {
    try {
      if (!win || win[MARK]) return true;
      win[MARK] = true;

      const E = win.Event && win.Event.prototype;
      if (E && !E.__aiPasteUnlockPreventDefault) {
        const originalPreventDefault = E.preventDefault;
        Object.defineProperty(E, '__aiPasteUnlockPreventDefault', { value: originalPreventDefault });
        E.preventDefault = function() {
          if (isPasteLikeEvent(this)) return;
          return originalPreventDefault.apply(this, arguments);
        };
      }

      const targets = [win, win.document, win.document && win.document.documentElement, win.document && win.document.body].filter(Boolean);
      const swallow = function(e) {
        if (!isPasteLikeEvent(e)) return;
        try { e.stopImmediatePropagation(); } catch {}
        try { e.stopPropagation(); } catch {}
        // Intentionally do not call preventDefault(): Chromium should still perform native paste.
      };

      for (const target of targets) {
        for (const name of ['keydown', 'keypress', 'keyup', 'paste', 'beforeinput']) {
          try { target.addEventListener(name, swallow, true); } catch {}
        }
      }

      try {
        for (const el of win.document.querySelectorAll('[onpaste],[onkeydown],[onkeypress],[onkeyup]')) {
          try { el.onpaste = null; } catch {}
          try { el.onkeydown = null; } catch {}
          try { el.onkeypress = null; } catch {}
          try { el.onkeyup = null; } catch {}
          try { el.removeAttribute('onpaste'); } catch {}
        }
      } catch {}
    } catch {}
    return true;
  }

  function walk(win, seen) {
    if (!win || seen.has(win)) return;
    seen.add(win);
    unlockWindow(win);
    let frames = [];
    try { frames = Array.from(win.frames || []); } catch {}
    for (const f of frames) {
      try { walk(f, seen); } catch {}
    }
  }

  walk(window, new Set());
  try {
    const mo = new MutationObserver(() => walk(window, new Set()));
    mo.observe(document.documentElement || document, { childList: true, subtree: true });
  } catch {}
  return true;
})()
""";

            try
            {
                await SendChannelAsync(ws, "Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object?>
                {
                    ["source"] = pasteUnlockScript
                }, timeout.Token);
            }
            catch
            {
            }

            var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = pasteUnlockScript,
                ["returnByValue"] = true,
                ["awaitPromise"] = true,
                ["userGesture"] = true
            }, timeout.Token);

            try
            {
                return eval.RootElement
                    .GetProperty("result")
                    .GetProperty("result")
                    .GetProperty("value")
                    .GetBoolean();
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> VerifyTextPresentAsync(ClientWebSocket ws, string answer, CancellationToken ct)
        {
            string probe = answer.Trim();
            if (probe.Length > 40) probe = probe.Substring(0, 40);
            if (probe.Length < 2) probe = answer.Trim();

            string probeJson = JsonSerializer.Serialize(probe);
            string expression = $$$"""
(() => {
  const PROBE = {{{probeJson}}};
  if (!PROBE) return false;
  function norm(s) { return String(s || '').replace(/\s+/g, ' ').trim(); }
  function has(s) { return norm(s).includes(norm(PROBE)); }
  function checkDoc(doc) {
    for (const el of doc.querySelectorAll('textarea,input,[contenteditable],[role=textbox]')) {
      if (has(el.value) || has(el.innerText) || has(el.textContent)) return true;
    }
    for (const f of doc.querySelectorAll('iframe')) {
      try {
        const d = f.contentDocument || f.contentWindow.document;
        if (d && checkDoc(d)) return true;
      } catch {}
    }
    return false;
  }
  return checkDoc(document);
})()
""";

            try
            {
                var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
                {
                    ["expression"] = expression,
                    ["returnByValue"] = true,
                    ["awaitPromise"] = true
                }, ct);
                return eval.RootElement
                    .GetProperty("result")
                    .GetProperty("result")
                    .GetProperty("value")
                    .GetBoolean();
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string> DiagnosePageWebSocketAsync(string wsUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            await SendChannelAsync(ws, "Runtime.enable", null, timeout.Token);

            string expression = """
(() => {
  function safe(s) { return String(s || '').replace(/\s+/g, ' ').trim().slice(0, 180); }
  function rect(el) {
    try {
      const r = el.getBoundingClientRect();
      return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
    } catch { return null; }
  }
  function visible(el) {
    try {
      const st = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      return st.display !== 'none' && st.visibility !== 'hidden' && Number(st.opacity) !== 0 &&
             r.width > 1 && r.height > 1 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
    } catch { return false; }
  }
  function info(el, idx) {
    const tag = (el.tagName || '').toLowerCase();
    return {
      idx,
      tag,
      id: el.id || '',
      name: el.getAttribute('name') || '',
      cls: safe(el.className),
      type: el.getAttribute('type') || '',
      role: el.getAttribute('role') || '',
      placeholder: el.getAttribute('placeholder') || '',
      aria: el.getAttribute('aria-label') || '',
      contenteditable: el.getAttribute('contenteditable') || '',
      isContentEditable: !!el.isContentEditable,
      disabled: !!el.disabled,
      readonly: !!el.readOnly,
      visible: visible(el),
      rect: rect(el),
      text: safe(el.innerText || el.textContent || el.value || '')
    };
  }

  const selectors = [
    'textarea',
    'input',
    '[contenteditable]',
    '[role=textbox]',
    '.edui-editor',
    '.edui-editor-iframe',
    '.edui-body-container',
    '.ql-editor',
    '.tox-edit-area',
    '.w-e-text',
    'iframe',
    'object',
    'embed'
  ];

  const found = [];
  const seen = new Set();
  for (const sel of selectors) {
    for (const el of document.querySelectorAll(sel)) {
      if (seen.has(el)) continue;
      seen.add(el);
      found.push(info(el, found.length));
    }
  }

  const frames = Array.from(document.querySelectorAll('iframe')).map((f, i) => {
    const item = info(f, i);
    item.src = f.src || f.getAttribute('src') || '';
    try {
      const d = f.contentDocument || f.contentWindow.document;
      item.accessible = true;
      item.innerTitle = d.title || '';
      item.innerBodyText = safe(d.body && d.body.innerText);
      item.innerEditables = Array.from(d.querySelectorAll('textarea,input,[contenteditable],[role=textbox]')).map((x, j) => info(x, j));
    } catch (e) {
      item.accessible = false;
      item.error = String(e && e.message || e);
    }
    return item;
  });

  return JSON.stringify({
    href: location.href,
    title: document.title,
    active: document.activeElement ? info(document.activeElement, -1) : null,
    bodyTextHead: safe(document.body && document.body.innerText).slice(0, 500),
    candidates: found,
    frames
  }, null, 2);
})()
""";

            var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
            }, timeout.Token);

            return ExtractRuntimeString(eval);
        }

        private static async Task<AutomationPageSnapshot> GetAutomationSnapshotFromPageAsync(string wsUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            await SendChannelAsync(ws, "Runtime.enable", null, timeout.Token);
            const string expression = """
(() => {
  const MAX = 4200;
  const badRe = /(\u4e0a\u4e00\u9898|\u4e0b\u4e00\u9898|\u4ea4\u5377|\u63d0\u4ea4|\u8fd4\u56de|\u9000\u51fa|\u5012\u8ba1\u65f6|\u8003\u8bd5\u987b\u77e5|\u4fdd\u5b58|\u6807\u8bb0|\u672a\u7b54|\u5df2\u7b54|\u7b54\u9898\u5361|\u9898\u53f7|\u767b\u5f55|\u5237\u65b0|\u5173\u95ed|\u5168\u5c4f|\u6536\u8d77|\u5c55\u5f00|\u5f53\u524d\u9898\u76ee|\u5df2\u4f5c\u7b54|\u672a\u4f5c\u7b54|remainTime|monitorStatus)/;
  const hardNavRe = /(\u5f53\u524d\u9898\u76ee\s*\u5df2\u4f5c\u7b54\s*\u672a\u4f5c\u7b54|\u7b54\u9898\u5361|\u672a\u4f5c\u7b54|\u5df2\u4f5c\u7b54|\u9898\u53f7\u5217\u8868|\u5f53\u524d\u9898\u76ee)/;
  const sectionTitleRe = /^[\u4e00\u4e8c\u4e09\u56db\u4e94\u516d\u4e03\u516b\u4e5d\u5341]+、\s*(\u5355\u9009\u9898|\u591a\u9009\u9898|\u586b\u7a7a\u9898|\u5224\u65ad\u9898|\u7b80\u7b54\u9898|\u8bba\u8ff0\u9898)\s*[\uff08(]?\d+(?:\.\d+)?\u5206?[\uff09)]?$/;
  const toolbarRe = /^(\u6bb5\u843d|arial|sans-serif|serif|\u5fae\u8f6f\u96c5\u9ed1|\u5b8b\u4f53|\u9ed1\u4f53|(?:1[0-9]|2[0-9]|[8-9])px)$/i;
  const qRe = /(\u5355\u9009|\u591a\u9009|\u5224\u65ad|\u586b\u7a7a|\u7b80\u7b54|\u8bba\u8ff0|\u6750\u6599|\u9605\u8bfb|\u9898|\u95ee\u9898|\u8bf7\u9009\u62e9|\u6b63\u786e\u7684\u662f|\u9519\u8bef\u7684\u662f|\u4e0b\u5217|\u7b54\u6848|A[\.．、]|B[\.．、]|C[\.．、]|D[\.．、]|\u2460|\u2461|\u2462|\u2463)/i;
  function norm(s) {
    return String(s || '').replace(/\r/g, '\n')
      .split('\n').map(x => x.replace(/[ \t\f\v]+/g, ' ').trim())
      .filter(x => x && !toolbarRe.test(x))
      .join('\n').replace(/\n{3,}/g, '\n\n').trim();
  }
  function lines(s) { return norm(s).split('\n').map(x => x.trim()).filter(Boolean); }
  function lineCount(s) { return lines(s).length; }
  function standaloneNumberCount(s) { return (norm(s).match(/^\d{1,3}$/gm) || []).length; }
  function navLike(s) {
    s = norm(s);
    const lc = Math.max(1, lineCount(s));
    const nums = standaloneNumberCount(s);
    if (hardNavRe.test(s)) return true;
    if (nums >= 8 && nums >= lc * 0.35) return true;
    if (nums >= 20) return true;
    return false;
  }
  function visible(el) {
    if (!el || el.nodeType !== 1) return false;
    const st = getComputedStyle(el);
    if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
  }
  function cssPath(el) {
    if (!el || !el.tagName) return '';
    if (el.id) return '#' + CSS.escape(el.id);
    const parts = [];
    while (el && el.nodeType === 1 && el !== document.body && parts.length < 6) {
      let p = el.tagName.toLowerCase();
      if (el.className && typeof el.className === 'string') {
        const cls = el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2);
        if (cls.length) p += '.' + cls.map(CSS.escape).join('.');
      }
      const parent = el.parentElement;
      if (parent) {
        const sib = Array.from(parent.children).filter(x => x.tagName === el.tagName);
        if (sib.length > 1) p += `:nth-of-type(${sib.indexOf(el) + 1})`;
      }
      parts.unshift(p);
      el = parent;
    }
    return parts.join(' > ');
  }
  function textOf(el) {
    if (!el) return '';
    return norm(el.innerText || el.textContent || el.value || el.getAttribute('aria-label') || el.getAttribute('title') || '');
  }
  function currentQuestionNumber() {
    try {
      const u = new URL(location.href);
      const start = u.searchParams.get('start');
      if (start && /^\d+$/.test(start)) return String(Number(start) + 1);
    } catch {}
    return null;
  }
  function cleanQuestionText(s, qn) {
    s = norm(s);
    if (!s) return '';
    if (qn) {
      const re = new RegExp('(?:^|\\n)\\s*' + qn.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*[\\.\\uFF0E\\u3001]?\\s*(?:\\n|\\(|\\uFF08|$)');
      const m = re.exec(s);
      if (m && m.index > 0) s = s.slice(m.index).trim();
    }
    const raw = lines(s);
    const kept = [];
    for (let i = 0; i < raw.length; i++) {
      let ln = raw[i];
      if (toolbarRe.test(ln) || /^\d{1,3}$/.test(ln) || sectionTitleRe.test(ln) || badRe.test(ln)) continue;
      if (/^[A-H]$/i.test(ln) && i + 1 < raw.length) {
        const next = raw[i + 1];
        if (next && !/^[A-H]$/i.test(next) && !badRe.test(next)) {
          kept.push(ln.toUpperCase() + '. ' + next);
          i++;
          continue;
        }
      }
      kept.push(ln);
    }
    return kept.join('\n').slice(0, MAX).trim();
  }
  function optionCountInText(s) { return (norm(s).match(/[A-H][\\.\\uFF0E\\u3001]/gi) || []).length; }
  function optionElements(root) {
    return Array.from(root.querySelectorAll('[onclick*="addChoice"], .answerBg, .singleoption, .multioption, .addChoice, label, li, .option, [class*="option"], [class*="xuan"], input[type=radio], input[type=checkbox]')).filter(visible);
  }
  function inputElements(root) {
    return Array.from(root.querySelectorAll('textarea, input:not([type]), input[type=text], input[type=search], iframe[id^="ueditor"], iframe, [contenteditable=true], [role=textbox]'))
      .filter(el => visible(el) || (el.tagName === 'TEXTAREA' && /^answer\d+/.test(el.id || el.name || '')));
  }
  function scoreElement(el, qn) {
    const raw = textOf(el);
    const cleaned = cleanQuestionText(raw, qn);
    if (cleaned.length < 8) return { score: -9999, raw, cleaned, reason: 'too-short' };
    if (navLike(raw) || navLike(cleaned)) return { score: -9999, raw, cleaned, reason: 'nav-like' };
    const opts = optionCountInText(cleaned);
    const optEls = optionElements(el).length;
    const inputs = inputElements(el).length;
    let score = 0;
    if (qRe.test(cleaned)) score += 35;
    score += Math.min(cleaned.length / 25, 30);
    score += Math.min(opts, 6) * 15;
    score += Math.min(optEls, 6) * 10;
    score += Math.min(inputs, 3) * 25;
    if (/(\u5355\u9009\u9898|\u591a\u9009\u9898|\u5224\u65ad\u9898|\u586b\u7a7a\u9898|\u7b80\u7b54\u9898|\u8bf7\u9009\u62e9|\u6b63\u786e\u7684\u662f|\u9519\u8bef\u7684\u662f|\u4e0b\u5217)/.test(cleaned)) score += 20;
    if (badRe.test(raw)) score -= 80;
    const r = el.getBoundingClientRect();
    const centerX = (r.left + r.right) / 2;
    if (centerX > innerWidth * 0.35) score += 18; else score -= 25;
    const area = Math.max(1, r.width * r.height);
    if (area > innerWidth * innerHeight * 0.75) score -= 35;
    if (cleaned.length > 3000 && opts < 2 && inputs === 0) score -= 25;
    return { score, raw, cleaned, reason: '' };
  }
  function inferType(text, optionTexts, inputs) {
    const s = norm(text + '\n' + optionTexts.join('\n'));
    if (/(\u591a\u9009|\u591a\u9879|\u4e0d\u5b9a\u9879|\u81f3\u5c11.*\u9879)/.test(s)) return 'multiple';
    if (/(\u5224\u65ad)/.test(s) || (optionTexts.length >= 2 && optionTexts.length <= 3 && /(\u6b63\u786e|\u9519\u8bef|\u5bf9|\u9519|\u662f|\u5426)/.test(optionTexts.join('\n')))) return 'judge';
    if (optionTexts.length >= 2 || optionCountInText(s) >= 2) return 'single';
    if (/(\u586b\u7a7a|\u586b\u5165|\u7a7a\u683c|____|\uFF3F|\uFF08\uFF09|\(\))/.test(s) || inputs.some(x => x.kind === 'input')) return 'blank';
    if (/(\u7b80\u7b54|\u8bba\u8ff0|\u95ee\u7b54|\u5206\u6790|\u8bf4\u660e|\u9610\u8ff0)/.test(s) || inputs.some(x => x.kind === 'textarea' || x.kind === 'contenteditable' || x.kind === 'iframe')) return 'essay';
    return 'unknown';
  }
  const qn = currentQuestionNumber();
  const selector = 'article, section, main, form, table, tbody, tr, td, li, [class*="question"], [class*="ques"], [class*="TiMu"], [class*="timu"], [class*="stem"], [class*="subject"], [id*="question"], [id*="ques"], div';
  const candidates = Array.from(document.querySelectorAll(selector)).filter(visible).map(el => {
    const scored = scoreElement(el, qn);
    return { el, ...scored, selector: cssPath(el) };
  }).filter(x => x.score > 10).sort((a, b) => b.score - a.score || a.cleaned.length - b.cleaned.length);
  if (!candidates.length) {
    return JSON.stringify({ questionText: '', questionType: 'unknown', options: [], inputCount: 0, href: location.href, title: document.title, diagnostic: '\u672a\u80fd\u5b9a\u4f4d\u5f53\u524d\u9898\u9762\uff1a\u5019\u9009\u5bb9\u5668\u4e3a\u7a7a\u6216\u5747\u4e3a\u5bfc\u822a/\u7b54\u9898\u5361\u3002' });
  }
  const picked = candidates[0];
  const root = picked.el;
  const options = [];
  const seen = new Set();
  for (const el of optionElements(root)) {
    let clickEl = el;
    let label = el.closest('label') || el.closest('li') || el.parentElement || el;
    if (el.matches && el.matches('input[type=radio],input[type=checkbox]')) {
      clickEl = el;
      const id = el.id;
      if (id) label = root.querySelector(`label[for="${CSS.escape(id)}"]`) || label;
    }
    let text = textOf(label);
    if (!text || text.length > 500 || navLike(text) || badRe.test(text)) text = textOf(el);
    if (!text || text.length > 500 || navLike(text) || badRe.test(text)) continue;
    const m = /^([A-H])[\s\.\uFF0E\u3001:\uFF1A)]*/i.exec(text);
    const key = m ? m[1].toUpperCase() : String.fromCharCode(65 + options.length);
    const sig = key + '|' + text;
    if (seen.has(sig)) continue;
    seen.add(sig);
    options.push({ key, text, selector: cssPath(clickEl), index: options.length });
    if (options.length >= 10) break;
  }
  const inputs = inputElements(root).map((el, i) => ({ selector: cssPath(el), index: i, kind: el.tagName === 'TEXTAREA' ? 'textarea' : (el.tagName === 'IFRAME' ? 'iframe' : (el.isContentEditable ? 'contenteditable' : 'input')) }));
  let questionText = picked.cleaned;
  if (!questionText || navLike(questionText)) questionText = '';
  const type = inferType(questionText, options.map(o => o.text), inputs);
  return JSON.stringify({
    questionText,
    questionType: type,
    options,
    inputCount: inputs.length,
    href: location.href,
    title: document.title,
    containerSelector: picked.selector,
    diagnostic: `score=${picked.score.toFixed(1)} reason=${picked.reason || 'picked'} raw=${picked.raw.slice(0, 300).replace(/\n/g, ' / ')}`
  });
})()

""";

            var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
            }, timeout.Token);

            string json = ExtractRuntimeString(eval);
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("读取失败。");
            return JsonSerializer.Deserialize<AutomationPageSnapshot>(json, JsonOpts())
                   ?? throw new InvalidOperationException("解析失败。");
        }

        private static async Task<AutomationActionResult> ApplyAutomationTextInPageAsync(string wsUrl, string answer, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            await SendChannelAsync(ws, "Runtime.enable", null, timeout.Token);
            string answerJson = JsonSerializer.Serialize(answer);
            string expression = $$"""
(() => {
  const ANSWER = {{answerJson}};
  function result(ok, reason, action, extra) {
    return JSON.stringify(Object.assign({
      ok,
      success: ok,
      reason: reason || '',
      message: reason || '',
      action: action || '',
      answer: String(ANSWER || '')
    }, extra || {}));
  }
  try {
    function norm(s) { return String(s || '').replace(/\r/g, '\n').replace(/[ \t\f\v]+/g, ' ').trim(); }
    function visible(el) {
      if (!el || el.nodeType !== 1) return false;
      const st = getComputedStyle(el);
      if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
      const r = el.getBoundingClientRect();
      return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
    }
    function cssPath(el) {
      if (!el || !el.tagName) return '';
      if (el.id) return '#' + CSS.escape(el.id);
      const parts = [];
      while (el && el.nodeType === 1 && el !== document.body && parts.length < 6) {
        let p = el.tagName.toLowerCase();
        if (el.className && typeof el.className === 'string') {
          const cls = el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2);
          if (cls.length) p += '.' + cls.map(CSS.escape).join('.');
        }
        const parent = el.parentElement;
        if (parent) {
          const sib = Array.from(parent.children).filter(x => x.tagName === el.tagName);
          if (sib.length > 1) p += `:nth-of-type(${sib.indexOf(el) + 1})`;
        }
        parts.unshift(p);
        el = parent;
      }
      return parts.join(' > ');
    }
    function fire(el) { for (const t of ['input', 'change', 'blur']) el.dispatchEvent(new Event(t, { bubbles: true })); }
    function click(el) {
      if (!el) return false;
      el.scrollIntoView({ block: 'center', inline: 'center' });
      el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
      el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
      el.click();
      el.dispatchEvent(new Event('change', { bubbles: true }));
      try {
        const qid = (el.querySelector && el.querySelector('[qid]') && el.querySelector('[qid]').getAttribute('qid'))
          || (el.getAttribute && el.getAttribute('qid'))
          || '';
        const hidden = qid ? document.getElementById('answer' + qid) : null;
        if (typeof window.addChoice === 'function' && el.matches && el.matches('[onclick*="addChoice"], .answerBg, .singleoption, .multioption') && hidden && !hidden.value) {
          window.addChoice(el);
        }
      } catch {}
      return true;
    }
    function textOf(el) { return norm(el && (el.innerText || el.textContent || el.value || el.getAttribute('aria-label') || el.getAttribute('title') || '')); }
    const badRe = /(\u4e0a\u4e00\u9898|\u4e0b\u4e00\u9898|\u4ea4\u5377|\u63d0\u4ea4|\u7b54\u9898\u5361|\u5f53\u524d\u9898\u76ee|\u5df2\u4f5c\u7b54|\u672a\u4f5c\u7b54|\u9898\u53f7|\u5012\u8ba1\u65f6)/;
    function navLike(s) {
      s = norm(s);
      if (badRe.test(s)) return true;
      const lines = s.split('\n').map(x => x.trim()).filter(Boolean);
      const nums = (s.match(/^\d{1,3}$/gm) || []).length;
      return nums >= 8 && nums >= Math.max(1, lines.length) * 0.35;
    }
    function optionCountInText(s) { return (norm(s).match(/[A-H][\.\uFF0E\u3001]/gi) || []).length; }
    function pickRoot() {
      const selector = 'article, section, main, form, table, tbody, tr, td, li, [class*="question"], [class*="ques"], [class*="TiMu"], [class*="timu"], [class*="stem"], [class*="subject"], [id*="question"], [id*="ques"], div';
      const candidates = Array.from(document.querySelectorAll(selector)).filter(visible).map(el => {
        const text = textOf(el);
        if (!text || navLike(text)) return { el, score: -9999, text };
        let score = Math.min(text.length / 25, 25) + Math.min(optionCountInText(text), 6) * 15;
        score += Array.from(el.querySelectorAll('[onclick*="addChoice"], .answerBg, .singleoption, .multioption, .addChoice, label, li, .option, [class*="option"], [class*="xuan"], input[type=radio], input[type=checkbox]')).filter(visible).length * 8;
        score += Array.from(el.querySelectorAll('textarea, input:not([type]), input[type=text], input[type=search], [contenteditable=true], [role=textbox]')).filter(visible).length * 20;
        const r = el.getBoundingClientRect();
        if ((r.left + r.right) / 2 > innerWidth * 0.35) score += 18; else score -= 25;
        if (badRe.test(text)) score -= 80;
        return { el, score, text };
      }).filter(x => x.score > 10).sort((a, b) => b.score - a.score);
      return candidates.length ? { root: candidates[0].el, diagnostic: `root=${cssPath(candidates[0].el)} score=${candidates[0].score.toFixed(1)}` } : { root: document.body, diagnostic: 'root=document.body fallback' };
    }
    const pickedRoot = pickRoot();
    const root = pickedRoot.root;
    const answer = norm(ANSWER);
    if (!answer) return result(false, '\u0041\u0049 \u7b54\u6848\u4e3a\u7a7a\u3002', 'no-answer', { diagnostic: pickedRoot.diagnostic });

    const qidForInput = (document.querySelector('[name=questionId]') && document.querySelector('[name=questionId]').value)
      || (document.querySelector('[id^=jiandaId]') && document.querySelector('[id^=jiandaId]').value)
      || '';
    const visibleInput = Array.from(root.querySelectorAll('textarea, input:not([type]), input[type=text], input[type=search], iframe[id^="ueditor"], iframe, [contenteditable=true], [role=textbox]')).filter(visible)[0];
    const hiddenAnswerInput = qidForInput ? document.getElementById('answer' + qidForInput) : null;
    const input = visibleInput || hiddenAnswerInput;
    const optionEls = Array.from(root.querySelectorAll('[onclick*="addChoice"], .answerBg, .singleoption, .multioption, .addChoice, label, li, .option, [class*="option"], [class*="select"], [class*="xuan"], input[type=radio], input[type=checkbox]')).filter(visible);
    const options = [];
    const seen = new Set();
    for (const el of optionEls) {
      let clickEl = el;
      let label = el.closest('label') || el.closest('li') || el.parentElement || el;
      if (el.matches && el.matches('input[type=radio],input[type=checkbox]')) {
        clickEl = el;
        const id = el.id;
        if (id) label = root.querySelector(`label[for="${CSS.escape(id)}"]`) || label;
      } else {
        const choiceBox = el.closest('[onclick*="addChoice"], .answerBg, .singleoption, .multioption');
        if (choiceBox) clickEl = choiceBox;
      }
      const text = textOf(label) || textOf(el);
      if (!text || text.length > 500 || navLike(text)) continue;
      const m = /^([A-H])[\s\.\uFF0E\u3001:\uFF1A)]*/i.exec(text);
      const key = m ? m[1].toUpperCase() : String.fromCharCode(65 + options.length);
      const sig = key + '|' + text;
      if (seen.has(sig)) continue;
      seen.add(sig);
      options.push({ key, text, selector: cssPath(clickEl), clickEl });
      if (options.length >= 10) break;
    }

    const isChoice = options.length >= 2;
    const optionSummary = options.map(o => `${o.key}:${o.text.slice(0, 60)}`).join(' | ');
    if (isChoice) {
      let keys = [];
      const cleaned = answer.replace(/\u7b54\u6848[:\uFF1A]/g, ' ').replace(/\u9009\u9879/g, ' ');
      const letterMatches = cleaned.match(/[A-H]/gi) || [];
      if (letterMatches.length && !/[A-Ha-h]{8,}/.test(cleaned)) keys = Array.from(new Set(letterMatches.map(x => x.toUpperCase())));
      if (!keys.length) {
        const judgeTrue = /(\u6b63\u786e|\u5bf9|\u662f|true|\u221A|\u5c0d)/i.test(answer);
        const judgeFalse = /(\u9519\u8bef|\u9519|\u5426|false|\u00D7|\u932f)/i.test(answer);
        for (const opt of options) {
          const t = opt.text;
          if ((judgeTrue && /(\u6b63\u786e|\u5bf9|\u662f|true|\u221A|\u5c0d)/i.test(t)) || (judgeFalse && /(\u9519\u8bef|\u9519|\u5426|false|\u00D7|\u932f)/i.test(t))) keys.push(opt.key);
        }
      }
      if (!keys.length) {
        for (const opt of options) {
          const body = opt.text.replace(/^[A-H][\s\.\uFF0E\u3001:\uFF1A)]*/i, '').trim();
          if (body && answer.includes(body.slice(0, Math.min(body.length, 20)))) keys.push(opt.key);
        }
      }
      keys = Array.from(new Set(keys));
      const missing = keys.filter(k => !options.some(o => o.key === k));
      if (missing.length) return result(false, `\u672a\u627e\u5230\u9009\u9879 ${missing.join(',')}`, 'no-match', { candidates: optionSummary, diagnostic: pickedRoot.diagnostic });
      if (!keys.length) return result(false, '\u0041\u0049 \u7b54\u6848\u65e0\u6cd5\u660e\u786e\u6620\u5c04\u5230\u9009\u9879\u3002', 'no-match', { candidates: optionSummary, diagnostic: pickedRoot.diagnostic });
      const clicked = [];
      for (const key of keys) {
        const opt = options.find(o => o.key === key);
        if (!opt) continue;
        click(opt.clickEl);
        clicked.push(opt);
      }
      if (!clicked.length) return result(false, '\u672a\u627e\u5230\u53ef\u70b9\u51fb\u7684\u5339\u914d\u9009\u9879\u3002', 'no-match', { candidates: optionSummary, diagnostic: pickedRoot.diagnostic });
      const qid = (clicked[0].clickEl.querySelector && clicked[0].clickEl.querySelector('[qid]') && clicked[0].clickEl.querySelector('[qid]').getAttribute('qid'))
        || (clicked[0].clickEl.getAttribute && clicked[0].clickEl.getAttribute('qid'))
        || (document.querySelector('[name=questionId]') && document.querySelector('[name=questionId]').value)
        || '';
      const hiddenAnswer = qid ? document.getElementById('answer' + qid) : null;
      return result(true, '\u5df2\u9009\u62e9: ' + clicked.map(o => o.key).join(','), 'click-option', {
        matchedOption: clicked.map(o => `${o.key}:${o.text}`).join(' | '),
        selector: clicked.map(o => o.selector).join(' | '),
        candidates: optionSummary,
        diagnostic: pickedRoot.diagnostic + '; qid=' + qid + '; hiddenAnswer=' + (hiddenAnswer ? hiddenAnswer.value : '<none>')
      });
    }

    if (!input) return result(false, '\u627e\u4e0d\u5230\u8f93\u5165\u6846\u3002', 'no-input', { candidates: optionSummary, diagnostic: pickedRoot.diagnostic + '; qid=' + qidForInput });
    const html = '<p>' + answer.replace(/[&<>]/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[ch])).replace(/\n/g, '<br/>') + '<br/></p>';
    let wrote = [];
    try {
      const frame = input.tagName === 'IFRAME' ? input : root.querySelector('iframe[id^="ueditor"], iframe');
      if (frame && frame.contentDocument && frame.contentDocument.body) {
        frame.scrollIntoView({ block: 'center', inline: 'center' });
        frame.contentDocument.body.innerHTML = html;
        frame.contentDocument.body.dispatchEvent(new Event('input', { bubbles: true }));
        frame.contentDocument.body.dispatchEvent(new Event('change', { bubbles: true }));
        wrote.push('iframe=' + cssPath(frame));
      }
    } catch (e) {
      wrote.push('iframe-error=' + (e && e.message ? e.message : String(e)));
    }
    const answerField = hiddenAnswerInput || (input.tagName === 'TEXTAREA' ? input : null) || root.querySelector('textarea[id^="answer"], textarea[name^="answer"]');
    if (answerField) {
      answerField.value = html;
      fire(answerField);
      wrote.push('textarea=' + cssPath(answerField));
    } else if (input.isContentEditable) {
      input.scrollIntoView({ block: 'center', inline: 'center' });
      input.focus();
      input.innerHTML = html;
      fire(input);
      wrote.push('contenteditable=' + cssPath(input));
    } else if ('value' in input && input.tagName !== 'IFRAME') {
      input.scrollIntoView({ block: 'center', inline: 'center' });
      input.focus();
      input.value = answer;
      fire(input);
      wrote.push('input=' + cssPath(input));
    }
    if (!wrote.length) return result(false, '\u627e\u5230\u8f93\u5165\u5bb9\u5668\u4f46\u5199\u5165\u5931\u8d25\u3002', 'fill-input-failed', { selector: cssPath(input), diagnostic: pickedRoot.diagnostic + '; qid=' + qidForInput });
    return result(true, '\u5df2\u586b\u5199\u7b54\u6848\u3002', 'fill-input', { selector: wrote.join(' | '), diagnostic: pickedRoot.diagnostic + '; qid=' + qidForInput });
  } catch (e) {
    return result(false, 'exception: ' + (e && e.message ? e.message : String(e)), 'exception', { stack: e && e.stack ? String(e.stack).slice(0, 800) : '' });
  }
})()

""";

            var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
            }, timeout.Token);

            return ParseActionResultFromPage(eval);
        }

        private static async Task<AutomationActionResult> ClickAutomationNextInPageAsync(string wsUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await SendChannelAsync(ws, "Runtime.enable", null, timeout.Token);
            const string expression = """
(() => {
  function result(ok, reason, action, extra) {
    return JSON.stringify(Object.assign({ ok, success: ok, reason, message: reason, action }, extra || {}));
  }
  try {
    const nextKeywords = ['\u4e0b\u4e00\u9898', '\u4e0b\u4e00\u9875', '\u4e0b\u4e00\u6b65', 'Next', 'NEXT', 'next', '>'];
    const prevKeywords = ['\u4e0a\u4e00\u9898', '\u4e0a\u4e00\u9875', '\u4e0a\u4e00\u6b65', 'Prev', 'PREV', 'prev', 'Previous', 'previous', 'Back', 'back'];
    function hasAny(text, keywords) {
      text = String(text || '');
      return keywords.some(k => text.indexOf(k) >= 0);
    }
    function visible(el) {
      if (!el || el.nodeType !== 1) return false;
      const st = getComputedStyle(el);
      if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
      const r = el.getBoundingClientRect();
      return r.width > 2 && r.height > 2 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
    }
    function cssPath(el) {
      if (!el || !el.tagName) return '';
      if (el.id) return '#' + CSS.escape(String(el.id));
      const parts = [];
      while (el && el.nodeType === 1 && el !== document.body && parts.length < 5) {
        let p = String(el.tagName || '').toLowerCase();
        const clsText = typeof el.className === 'string' ? el.className : '';
        const cls = clsText.trim().split(' ').filter(Boolean).slice(0, 2);
        if (cls.length) p += '.' + cls.map(CSS.escape).join('.');
        const parent = el.parentElement;
        if (parent) {
          const sib = Array.from(parent.children).filter(x => x.tagName === el.tagName);
          if (sib.length > 1) p += ':nth-of-type(' + (sib.indexOf(el) + 1) + ')';
        }
        parts.unshift(p);
        el = parent;
      }
      return parts.join(' > ');
    }
    function ownText(el) {
      return String(el.innerText || el.textContent || el.value || el.title || el.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim();
    }
    function hintText(el) {
      return [
        ownText(el),
        String(el.id || ''),
        String(typeof el.className === 'string' ? el.className : ''),
        String(el.title || ''),
        String(el.getAttribute('aria-label') || '')
      ].join(' ').trim();
    }
    function disabled(el) {
      const cls = String(typeof el.className === 'string' ? el.className : '').toLowerCase();
      return !!(el.disabled || el.getAttribute('disabled') !== null || el.getAttribute('aria-disabled') === 'true' || cls.indexOf('disabled') >= 0 || cls.indexOf('disable') >= 0);
    }
    function isNextCandidate(el) {
      const hint = hintText(el);
      if (!hint) return false;
      if (hasAny(hint, prevKeywords)) return false;
      if (disabled(el)) return false;
      const lower = hint.toLowerCase();
      return hasAny(hint, nextKeywords) || lower.indexOf('next') >= 0;
    }
    function actionable(el) {
      if (!el) return el;
      const tag = String(el.tagName || '').toLowerCase();
      if ((tag === 'a' || tag === 'button' || tag === 'input') && isNextCandidate(el)) return el;
      const child = Array.from(el.querySelectorAll('a, button, input[type=button], input[type=submit], [role=button]')).find(x => visible(x) && isNextCandidate(x));
      return child || el;
    }
    function click(el) {
      el.scrollIntoView({ block: 'center', inline: 'center' });
      if (el.focus) el.focus();
      const events = ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'];
      for (const type of events) {
        if (type.indexOf('pointer') === 0 && !window.PointerEvent) continue;
        const ev = type.indexOf('pointer') === 0
          ? new PointerEvent(type, { bubbles: true, cancelable: true, view: window })
          : new MouseEvent(type, { bubbles: true, cancelable: true, view: window });
        el.dispatchEvent(ev);
      }
    }
    const selectors = 'button, a, input[type=button], input[type=submit], [role=button], .btn, [class*=btn], [class*=next], [class*=Next], span, div';
    const candidates = Array.from(document.querySelectorAll(selectors)).filter(visible).filter(isNextCandidate).map(el => {
      const text = ownText(el);
      const hint = hintText(el);
      const r = el.getBoundingClientRect();
      let score = 50;
      if (nextKeywords.includes(text)) score += 60;
      if (hasAny(hint, ['\u4e0b\u4e00\u9898', '\u4e0b\u4e00\u9875', '\u4e0b\u4e00\u6b65'])) score += 30;
      if (String(hint).toLowerCase().indexOf('next') >= 0) score += 20;
      if (r.left > innerWidth * 0.4) score += 15;
      if (r.top > innerHeight * 0.45) score += 10;
      if (text.length > 30) score -= 20;
      return { el, text, hint, score, selector: cssPath(el) };
    }).sort((a, b) => b.score - a.score);
    if (!candidates.length) {
      const visibleButtons = Array.from(document.querySelectorAll('button, a, input[type=button], input[type=submit], [role=button], .btn, [class*=btn]'))
        .filter(visible).slice(0, 20).map(el => ownText(el) || cssPath(el)).join(' | ');
      return result(false, '\u627e\u4e0d\u5230\u4e0b\u4e00\u9898\u6309\u94ae\u3002', 'no-next', { candidates: visibleButtons });
    }
    const item = candidates[0];
    const target = actionable(item.el);
    const beforeUrl = location.href;
    click(target);
    return result(true, '\u5df2\u70b9\u51fb\u4e0b\u4e00\u9898\u3002', 'click-next', {
      selector: cssPath(target),
      matchedOption: ownText(target),
      diagnostic: 'score=' + item.score + '; container=' + item.selector + '; target=' + cssPath(target) + '; onclick=' + String(target.getAttribute && target.getAttribute('onclick') || '') + '; beforeUrl=' + beforeUrl + '; afterUrl=' + location.href,
      candidates: candidates.slice(0, 10).map(x => x.score + ':' + x.text + ':' + x.selector).join(' | ')
    });
  } catch (e) {
    return result(false, 'exception: ' + (e && e.message ? e.message : String(e)), 'exception', { stack: e && e.stack ? String(e.stack).slice(0, 800) : '' });
  }
})()

""";

            var eval = await SendChannelAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
            }, timeout.Token);


            if (IsExecutionContextDestroyed(eval))
            {
                return new AutomationActionResult(true, "\u5df2\u70b9\u51fb\u4e0b\u4e00\u9898\uff0c\u9875\u9762\u6b63\u5728\u8df3\u8f6c\u3002");
            }

            return ParseActionResultFromPage(eval);
        }

        private static async Task<JsonDocument> SendChannelAsync(ClientWebSocket ws, string method, object? parameters, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _seq);
            var msg = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
            if (parameters != null) msg["params"] = parameters;

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            while (true)
            {
                string text = await ReceiveTextAsync(ws, ct);
                var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
                {
                    return doc;
                }
                doc.Dispose();
            }
        }

        private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string ExtractRuntimeString(JsonDocument doc)
        {
            try
            {
                return doc.RootElement
                    .GetProperty("result")
                    .GetProperty("result")
                    .GetProperty("value")
                    .GetString() ?? "";
            }
            catch { return ""; }
        }

        private static string ExtractAxNames(JsonDocument doc)
        {
            var parts = new List<string>();
            try
            {
                var nodes = doc.RootElement.GetProperty("result").GetProperty("nodes").EnumerateArray();
                foreach (var node in nodes)
                {
                    if (node.TryGetProperty("name", out var name) &&
                        name.TryGetProperty("value", out var value))
                    {
                        var s = value.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                    }
                    if (node.TryGetProperty("value", out var valNode) &&
                        valNode.TryGetProperty("value", out var val))
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                    }
                }
            }
            catch { }
            return string.Join("\n", parts.Distinct());
        }

        private static AutomationActionResult ParseActionResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new AutomationActionResult(false, "页面脚本无返回。");
            try
            {
                using var doc = JsonDocument.Parse(json);
                bool success =
                    (doc.RootElement.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.True) ||
                    (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True);
                string message = doc.RootElement.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "") : "";
                string reason = doc.RootElement.TryGetProperty("reason", out var reasonEl) ? (reasonEl.GetString() ?? "") : "";
                string action = doc.RootElement.TryGetProperty("action", out var actionEl) ? (actionEl.GetString() ?? "") : "";
                string selector = doc.RootElement.TryGetProperty("selector", out var selectorEl) ? (selectorEl.GetString() ?? "") : "";
                string matched = doc.RootElement.TryGetProperty("matchedOption", out var matchedEl) ? (matchedEl.GetString() ?? "") : "";
                string diagnostic = doc.RootElement.TryGetProperty("diagnostic", out var diagEl) ? (diagEl.GetString() ?? "") : "";

                var parts = new List<string>();
                parts.Add(!string.IsNullOrWhiteSpace(message) ? message : (!string.IsNullOrWhiteSpace(reason) ? reason : (success ? "成功。" : "失败。")));
                if (!string.IsNullOrWhiteSpace(action)) parts.Add("action=" + action);
                if (!string.IsNullOrWhiteSpace(matched)) parts.Add("matched=" + matched);
                if (!string.IsNullOrWhiteSpace(selector)) parts.Add("selector=" + selector);
                if (!string.IsNullOrWhiteSpace(diagnostic)) parts.Add("diag=" + diagnostic);
                return new AutomationActionResult(success, string.Join("；", parts));
            }
            catch
            {
                return new AutomationActionResult(false, json);
            }
        }

        private static AutomationActionResult ParseActionResultFromPage(JsonDocument doc)
        {
            string summary = "";
            try
            {
                summary = doc.RootElement.GetRawText();
                if (summary.Length > 1200) summary = summary[..1200];

                if (doc.RootElement.TryGetProperty("exceptionDetails", out var exceptionDetails))
                {
                    string text = exceptionDetails.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                    string description = "";
                    if (exceptionDetails.TryGetProperty("exception", out var ex) &&
                        ex.TryGetProperty("description", out var desc))
                    {
                        description = desc.GetString() ?? "";
                    }
                    return new AutomationActionResult(false, "页面脚本异常：" + (string.IsNullOrWhiteSpace(description) ? text : description));
                }

                if (!doc.RootElement.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("result", out var runtimeResult))
                {
                    return new AutomationActionResult(false, "返回结构异常：" + summary);
                }

                string json = "";
                if (runtimeResult.TryGetProperty("value", out var value))
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        json = value.GetString() ?? "";
                    }
                    else if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
                    {
                        json = value.GetRawText();
                    }
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    string type = runtimeResult.TryGetProperty("type", out var typeEl) ? (typeEl.GetString() ?? "") : "";
                    string subtype = runtimeResult.TryGetProperty("subtype", out var subtypeEl) ? (subtypeEl.GetString() ?? "") : "";
                    string description = runtimeResult.TryGetProperty("description", out var descEl) ? (descEl.GetString() ?? "") : "";
                    return new AutomationActionResult(false, $"页面脚本无返回。type={type} subtype={subtype} desc={description} raw={summary}");
                }

                return ParseActionResult(json);
            }
            catch (Exception ex)
            {
                return new AutomationActionResult(false, "结果解析异常：" + ex.Message + (string.IsNullOrWhiteSpace(summary) ? "" : " raw=" + summary));
            }
        }

        private static bool IsExecutionContextDestroyed(JsonDocument doc)
        {
            try
            {
                string raw = doc.RootElement.GetRawText();
                return raw.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
                       || raw.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Replace("\r", "\n");
            s = Regex.Replace(s, @"[ \t\f\v]+", " ");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        private static string? GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static JsonSerializerOptions JsonOpts() => new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private sealed class PageTarget
        {
            public string Id { get; set; } = "";
            public string Type { get; set; } = "";
            public string Title { get; set; } = "";
            public string Url { get; set; } = "";
            public string? WebSocketDebuggerUrl { get; set; }
        }
    }

    public sealed class AutomationPageSnapshot
    {
        public string QuestionText { get; set; } = "";
        public string QuestionType { get; set; } = "";
        public List<AutomationOption> Options { get; set; } = new();
        public int InputCount { get; set; }
        public string Href { get; set; } = "";
        public string Title { get; set; } = "";
        public string TargetTitle { get; set; } = "";
        public string TargetUrl { get; set; } = "";
        public string ContainerSelector { get; set; } = "";
        public string Diagnostic { get; set; } = "";
    }

    public sealed class AutomationOption
    {
        public string Key { get; set; } = "";
        public string Text { get; set; } = "";
        public string Selector { get; set; } = "";
        public int Index { get; set; }
    }

    public sealed record AutomationActionResult(bool Success, string Message);
}
