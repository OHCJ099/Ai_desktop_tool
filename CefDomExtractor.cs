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

namespace AI_desktop_tool
{
    /// <summary>
    /// CEF/Chromium DevTools Protocol text extractor.
    ///
    /// This bypasses normal screenshot and Windows UIA limitations by reading the
    /// actual Chromium page runtime. The target CEF app must be launched with:
    ///   --remote-debugging-port=9222 --remote-allow-origins=*
    /// </summary>
    public static class CefDomExtractor
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static int _seq;

        public static Task<bool> IsCdpAvailableAsync(int port = 9222, CancellationToken ct = default)
        {
            return IsCdpAliveAsync(port, ct);
        }

        public static async Task<string> ExtractBestTextAsync(int port = 9222, CancellationToken ct = default)
        {
            await EnsureCdpAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0)
            {
                // Some CEF builds expose no /json/list entries until a web page is opened.
                // Try browser-level Target.getTargets as a fallback.
                targets = await GetTargetsViaBrowserAsync(port, ct);
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
                return "未发现可读取的 CEF 页面。请用带参数的方式启动考试端：CXExam.exe --remote-debugging-port=9222 --remote-allow-origins=*，进入题目页面后再点 C。";
            }

            return "已连接 CEF DevTools，但当前没有 page/webview target；请先进入包含题目的网页页面后再点 C。";
        }

        public static async Task<bool> TryInjectAnswerAsync(string answer, int port = 9222, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(answer)) return false;

            await EnsureCdpAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaBrowserAsync(port, ct);

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
                    if (await InjectIntoPageWebSocketAsync(t.WebSocketDebuggerUrl!, answer, ct))
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
            await EnsureCdpAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaBrowserAsync(port, ct);

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

        public static async Task<string> DumpInjectionDiagnosticsAsync(int port = 9222, CancellationToken ct = default)
        {
            await EnsureCdpAvailableAsync(port, ct);

            var targets = await GetTargetsAsync(port, ct);
            if (targets.Count == 0) targets = await GetTargetsViaBrowserAsync(port, ct);

            var sb = new StringBuilder();
            sb.AppendLine("CEF answer injection diagnostics");
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
                "cef_inject_diag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            string appPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "cef_inject_diag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");

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

        private static async Task EnsureCdpAvailableAsync(int port, CancellationToken ct)
        {
            if (await IsCdpAliveAsync(port, ct)) return;

            string[] candidates =
            {
                ExamShortcutHelper.ExamExePath,
                ExamShortcutHelper.DesktopExamExePath
            };

            string? exe = candidates.FirstOrDefault(File.Exists);
            if (exe == null) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                    Arguments = $"--remote-debugging-port={port} --remote-allow-origins=* --enable-logging --v=1",
                    UseShellExecute = true
                });
            }
            catch
            {
                return;
            }

            for (int i = 0; i < 30; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(300, ct);
                if (await IsCdpAliveAsync(port, ct)) return;
            }
        }

        private static async Task<bool> IsCdpAliveAsync(int port, CancellationToken ct)
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

        private static async Task<List<CdpTarget>> GetTargetsAsync(int port, CancellationToken ct)
        {
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
                return JsonSerializer.Deserialize<List<CdpTarget>>(json, JsonOpts()) ?? new List<CdpTarget>();
            }
            catch
            {
                return new List<CdpTarget>();
            }
        }

        private static async Task<List<CdpTarget>> GetTargetsViaBrowserAsync(int port, CancellationToken ct)
        {
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
                using var doc = JsonDocument.Parse(json);
                string? browserWs = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                if (string.IsNullOrWhiteSpace(browserWs)) return new List<CdpTarget>();

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(browserWs), ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                var resp = await SendCdpAsync(ws, "Target.getTargets", null, timeout.Token);
                var list = new List<CdpTarget>();
                if (resp.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("targetInfos", out var infos))
                {
                    foreach (var info in infos.EnumerateArray())
                    {
                        list.Add(new CdpTarget
                        {
                            Id = GetString(info, "targetId"),
                            Type = GetString(info, "type") ?? "",
                            Title = GetString(info, "title") ?? "",
                            Url = GetString(info, "url") ?? ""
                        });
                    }
                }
                return list;
            }
            catch
            {
                return new List<CdpTarget>();
            }
        }

        private static async Task<string> ExtractFromPageWebSocketAsync(string wsUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            await SendCdpAsync(ws, "Runtime.enable", null, timeout.Token);

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
  const badRe = /(上一题|下一题|交卷|提交|返回|退出|倒计时|考试须知|保存|标记|未答|已答|答题卡|题号|登录|刷新|关闭|全屏|收起|展开|当前题目|已作答|未作答|remainTime|monitorStatus)/;
  const toolbarRe = /^(段落|arial|sans-serif|serif|微软雅黑|宋体|黑体|(?:1[0-9]|2[0-9]|[8-9])px)$/i;
  const qRe = /(单选|多选|判断|填空|简答|论述|材料|阅读|题|问题|请选择|正确的是|错误的是|下列|答案|A[\.．、]|B[\.．、]|C[\.．、]|D[\.．、]|①|②|③|④)/i;
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

            var eval = await SendCdpAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
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
                    var ax = await SendCdpAsync(ws, "Accessibility.getFullAXTree", null, timeout.Token);
                    axText = ExtractAxNames(ax);
                }
                catch { }
            }

            return Normalize(domText + "\n" + axText);
        }

        private static async Task<bool> InjectIntoPageWebSocketAsync(string wsUrl, string answer, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            await SendCdpAsync(ws, "Runtime.enable", null, timeout.Token);

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
  function injectUEditor() {
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
  if (injectUEditor()) return true;

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
    if (/answer|daan|reply|editor|ueditor|content|textarea|subject|write|填|答|答案|作答|回答/.test(hint)) s += 80;
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

            var eval = await SendCdpAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
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
                    if (await VerifyAnswerPresentAsync(ws, answer, timeout.Token))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            // Last CDP-level attempt: focus the best editable element, select all, then use
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
    if (/answer|daan|reply|editor|ueditor|content|textarea|subject|write|填|答|答案|作答|回答/.test(hint)) s += 100;
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

            var focusEval = await SendCdpAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
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
                await SendCdpAsync(ws, "Input.dispatchMouseEvent", new Dictionary<string, object?>
                {
                    ["type"] = "mousePressed",
                    ["x"] = x,
                    ["y"] = y,
                    ["button"] = "left",
                    ["clickCount"] = 1
                }, timeout.Token);
                await SendCdpAsync(ws, "Input.dispatchMouseEvent", new Dictionary<string, object?>
                {
                    ["type"] = "mouseReleased",
                    ["x"] = x,
                    ["y"] = y,
                    ["button"] = "left",
                    ["clickCount"] = 1
                }, timeout.Token);
                await SendCdpAsync(ws, "Input.insertText", new Dictionary<string, object?>
                {
                    ["text"] = answer
                }, timeout.Token);
                return await VerifyAnswerPresentAsync(ws, answer, timeout.Token);
            }

            return false;
        }

        private static async Task<bool> EnablePasteInPageWebSocketAsync(string wsUrl, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            await SendCdpAsync(ws, "Runtime.enable", null, timeout.Token);
            try { await SendCdpAsync(ws, "Page.enable", null, timeout.Token); } catch { }

            const string pasteUnlockScript = """
(() => {
  const MARK = '__ai_desktop_tool_paste_unlock_20260701__';

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
                await SendCdpAsync(ws, "Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object?>
                {
                    ["source"] = pasteUnlockScript
                }, timeout.Token);
            }
            catch
            {
            }

            var eval = await SendCdpAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
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

        private static async Task<bool> VerifyAnswerPresentAsync(ClientWebSocket ws, string answer, CancellationToken ct)
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
                var eval = await SendCdpAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
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

            await SendCdpAsync(ws, "Runtime.enable", null, timeout.Token);

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

            var eval = await SendCdpAsync(ws, "Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
            }, timeout.Token);

            return ExtractRuntimeString(eval);
        }

        private static async Task<JsonDocument> SendCdpAsync(ClientWebSocket ws, string method, object? parameters, CancellationToken ct)
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

        private sealed class CdpTarget
        {
            public string Id { get; set; } = "";
            public string Type { get; set; } = "";
            public string Title { get; set; } = "";
            public string Url { get; set; } = "";
            public string? WebSocketDebuggerUrl { get; set; }
        }
    }
}
