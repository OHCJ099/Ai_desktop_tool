using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI_desktop_tool
{
    internal static class LocalQuestionBank
    {
        private const double MatchThreshold = 0.50;
        private static readonly object LoadLock = new();
        private static List<QuestionEntry>? _questions;

        public static async Task<LocalQuestionMatch?> SearchModernHistoryAsync(string queryText, string? questionType = null, IReadOnlyList<LocalSearchOption>? currentOptions = null)
        {
            if (string.IsNullOrWhiteSpace(queryText)) return null;

            var questions = await LoadAsync();
            if (questions.Count == 0) return null;

            string cleanQuery = CleanQuestionText(queryText);
            if (string.IsNullOrWhiteSpace(cleanQuery)) return null;

            QuestionEntry? best = null;
            double bestScore = 0;

            foreach (var q in questions)
            {
                double score = Similarity(cleanQuery, q.CleanPrompt);

                // If the caller knows the current question type, prefer same broad type.
                if (!string.IsNullOrWhiteSpace(questionType) && IsSameType(questionType, q.Type))
                {
                    score += 0.04;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = q;
                }
            }

            if (best == null || bestScore < MatchThreshold) return null;

            string output = best.GetPreferredOutput(currentOptions);
            if (string.IsNullOrWhiteSpace(output)) return null;

            return new LocalQuestionMatch(
                best.Id,
                best.Prompt,
                best.Type,
                output,
                Math.Min(1, bestScore));
        }

        private static async Task<List<QuestionEntry>> LoadAsync()
        {
            if (_questions != null) return _questions;

            string json;
            var asm = Assembly.GetExecutingAssembly();
            const string suffix = "Resources.modern_history_questions.json";
            string? resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(resourceName))
            {
                await using var stream = asm.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException("近代史题库资源无法打开。" + resourceName);
                using var reader = new StreamReader(stream);
                json = await reader.ReadToEndAsync();
            }
            else
            {
                string localPath = Path.Combine(AppContext.BaseDirectory, "Resources", "modern_history_questions.json");
                if (!File.Exists(localPath)) return new List<QuestionEntry>();
                json = await File.ReadAllTextAsync(localPath);
            }

            var raw = JsonSerializer.Deserialize<List<QuestionEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<QuestionEntry>();

            foreach (var q in raw)
            {
                q.CleanPrompt = CleanQuestionText(q.Prompt);
            }

            lock (LoadLock)
            {
                _questions ??= raw.Where(q => !string.IsNullOrWhiteSpace(q.Prompt)).ToList();
                return _questions;
            }
        }

        private static string CleanQuestionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string s = text;
            s = Regex.Replace(s, @"\r", "\n");
            s = Regex.Replace(s, @"(^|\n)\s*\d+\s*[\.、\)）]\s*", "$1");
            s = Regex.Replace(s, @"\[[^\]]*题\]|【[^】]*题】|\([^)]*题[^)]*\)|（[^）]*题[^）]*）", "");
            s = Regex.Replace(s, @"[A-H]\s*[\.、．]\s*[^\n]+", "");
            s = Regex.Replace(s, @"我的答案[:：].*", "");
            s = Regex.Replace(s, @"答案[:：].*", "");
            s = Regex.Replace(s, @"解析[:：].*", "");
            s = Regex.Replace(s, @"\s+", "");
            s = Regex.Replace(s, @"[\p{P}\p{S}]", "");
            s = s.Replace("一", "").Replace("二", "").Replace("三", "").Replace("四", "").Replace("五", "");
            return s.Trim();
        }

        private static double Similarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
            if (a == b) return 1;
            if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            {
                double min = Math.Min(a.Length, b.Length);
                double max = Math.Max(a.Length, b.Length);
                if (min >= 8) return Math.Max(0.72, min / max);
            }

            var aBigrams = Bigrams(a);
            var bBigrams = Bigrams(b);
            if (aBigrams.Count == 0 || bBigrams.Count == 0) return 0;

            int intersection = aBigrams.Count(bg => bBigrams.Contains(bg));
            int union = aBigrams.Count + bBigrams.Count - intersection;
            return union > 0 ? (double)intersection / union : 0;
        }

        private static HashSet<string> Bigrams(string s)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(s)) return set;
            if (s.Length == 1)
            {
                set.Add(s);
                return set;
            }
            for (int i = 0; i < s.Length - 1; i++)
            {
                set.Add(s.Substring(i, 2));
            }
            return set;
        }

        private static bool IsSameType(string a, string b)
        {
            string x = NormalizeType(a);
            string y = NormalizeType(b);
            return x.Length > 0 && x == y;
        }

        private static string NormalizeType(string type)
        {
            type = (type ?? "").ToLowerInvariant();
            if (type.Contains("single") || type.Contains("单选")) return "single";
            if (type.Contains("multi") || type.Contains("multiple") || type.Contains("多选")) return "multiple";
            if (type.Contains("judge") || type.Contains("判断")) return "judge";
            if (type.Contains("short") || type.Contains("简答")) return "short";
            if (type.Contains("comprehensive") || type.Contains("论述") || type.Contains("综合")) return "comprehensive";
            return type;
        }

        private static string CleanOptionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text;
            s = Regex.Replace(s, @"^[A-Ha-h]\s*[\.??)?]\s*", "");
            s = Regex.Replace(s, @"\s+", "");
            s = Regex.Replace(s, @"[\p{P}\p{S}]", "");
            return s.Trim();
        }

        private static double OptionSimilarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
            if (a == b) return 1;
            if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            {
                double min = Math.Min(a.Length, b.Length);
                double max = Math.Max(a.Length, b.Length);
                return Math.Max(0.75, min / max);
            }
            return Similarity(a, b);
        }

        private sealed class QuestionEntry
        {
            public string Id { get; set; } = "";
            public string Type { get; set; } = "";
            public string TypeLabel { get; set; } = "";
            public string Prompt { get; set; } = "";
            public List<OptionEntry> Options { get; set; } = new();
            public string AnswerText { get; set; } = "";
            public List<string> AnswerKeys { get; set; } = new();
            public string Explanation { get; set; } = "";
            public string CleanPrompt { get; set; } = "";

            public string GetPreferredOutput(IReadOnlyList<LocalSearchOption>? currentOptions)
            {
                bool isShort = Type.Equals("short", StringComparison.OrdinalIgnoreCase);

                if (isShort && !string.IsNullOrWhiteSpace(Explanation))
                {
                    return Explanation.Trim();
                }

                bool isComprehensive = Type.Equals("comprehensive", StringComparison.OrdinalIgnoreCase);

                if (isComprehensive)
                {
                    return !string.IsNullOrWhiteSpace(AnswerText) ? AnswerText.Trim() : (Explanation ?? "").Trim();
                }

                if (Type.Equals("judge", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(AnswerText)) return AnswerText.Trim();
                    return string.Join("", AnswerKeys).Trim();
                }

                if ((Type.Equals("single", StringComparison.OrdinalIgnoreCase) || Type.Equals("multiple", StringComparison.OrdinalIgnoreCase))
                    && AnswerKeys.Count > 0)
                {
                    string remapped = RemapChoiceAnswerToCurrentOptions(currentOptions);
                    if (!string.IsNullOrWhiteSpace(remapped)) return remapped;
                    return string.Join("", AnswerKeys).Trim();
                }

                return (AnswerText ?? "").Trim();
            }

            private string RemapChoiceAnswerToCurrentOptions(IReadOnlyList<LocalSearchOption>? currentOptions)
            {
                if (currentOptions == null || currentOptions.Count == 0 || Options.Count == 0 || AnswerKeys.Count == 0)
                {
                    return "";
                }

                var mapped = new List<string>();
                foreach (string answerKey in AnswerKeys)
                {
                    var bankOption = Options.FirstOrDefault(o => o.Key.Equals(answerKey, StringComparison.OrdinalIgnoreCase));
                    if (bankOption == null || string.IsNullOrWhiteSpace(bankOption.Text)) return "";

                    LocalSearchOption? best = null;
                    double bestScore = 0;
                    string bankText = CleanOptionText(bankOption.Text);

                    foreach (var current in currentOptions)
                    {
                        if (string.IsNullOrWhiteSpace(current.Key) || string.IsNullOrWhiteSpace(current.Text)) continue;
                        double score = OptionSimilarity(bankText, CleanOptionText(current.Text));
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = current;
                        }
                    }

                    if (best == null || bestScore < 0.55) return "";
                    mapped.Add(best.Key.Trim().ToUpperInvariant());
                }

                return string.Concat(mapped.Distinct().OrderBy(x => x, StringComparer.Ordinal));
            }
        }

        private sealed class OptionEntry
        {
            public string Key { get; set; } = "";
            public string Text { get; set; } = "";
        }
    }

    internal sealed record LocalQuestionMatch(
        string Id,
        string Prompt,
        string Type,
        string Answer,
        double Similarity);

    internal sealed record LocalSearchOption(string Key, string Text);
}
