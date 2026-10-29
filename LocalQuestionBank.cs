using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WindowsClient
{
    internal static class LocalQuestionBank
    {
        private const double MatchThreshold = 0.50;
        private static readonly object LoadLock = new();
        private static List<QuestionEntry>? _questions;

        private static readonly string[] PackedFiles =
        {
            "question_bank.bin"
        };

        public static async Task<LocalQuestionMatch?> SearchLocalAsync(string queryText, string? questionType = null, IReadOnlyList<LocalSearchOption>? currentOptions = null)
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

            var all = new List<QuestionEntry>();
            var asm = Assembly.GetExecutingAssembly();
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            foreach (string file in PackedFiles)
            {
                string? resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("Resources." + file, StringComparison.OrdinalIgnoreCase));

                string json = "";
                if (resourceName != null)
                {
                    await using var stream = asm.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException("resource-open-failed:" + resourceName);
                    json = await ReadPackedJsonAsync(stream);
                }
                else
                {
                    string packedPath = Path.Combine(AppContext.BaseDirectory, "Resources", file);
                    if (File.Exists(packedPath))
                    {
                        await using var stream = File.OpenRead(packedPath);
                        json = await ReadPackedJsonAsync(stream);
                    }
                    else
                    {
                        string jsonFile = file.Replace(".bin", ".json");
                        string localPath = Path.Combine(AppContext.BaseDirectory, "Resources", jsonFile);
                        if (File.Exists(localPath))
                        {
                            json = await File.ReadAllTextAsync(localPath);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var list = JsonSerializer.Deserialize<List<QuestionEntry>>(json, options);
                    if (list != null)
                    {
                        all.AddRange(list);
                    }
                }
            }

            foreach (var q in all)
            {
                q.CleanPrompt = CleanQuestionText(q.Prompt);
            }

            lock (LoadLock)
            {
                _questions ??= all.Where(q => !string.IsNullOrWhiteSpace(q.Prompt)).ToList();
                return _questions;
            }
        }

        private static async Task<string> ReadPackedJsonAsync(Stream stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            byte[] packed = ms.ToArray();
            for (int i = 0; i < packed.Length; i++) packed[i] ^= 0x5A;
            await using var input = new MemoryStream(packed);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return await reader.ReadToEndAsync();
        }

        private static string CleanQuestionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string s = text;
            s = Regex.Replace(s, @"\r", "\n");
            s = Regex.Replace(s, @"(^|\n)\s*\d+\s*[\.、\)）]\s*", "$1");
            s = Regex.Replace(s, @"\[[^\]]*题\]|【[^】]*题】|\([^)]*题[^)]*\)|（[^）]*题[^）]*）", "");
            s = Regex.Replace(s, @"[A-H]\s*[\.、．]\s*[^\n]+", "");
            s = Regex.Replace(s, string.Concat("\u6211", "\u7684", "\u7b54", "\u6848", @"[:：].*"), "");
            s = Regex.Replace(s, string.Concat("\u7b54", "\u6848", @"[:：].*"), "");
            s = Regex.Replace(s, string.Concat("\u89e3", "\u6790", @"[:：].*"), "");
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
            // A fuzzy phrase-order match must not tie an exact option text match.
            return Math.Min(0.99, Similarity(a, b));
        }

        private sealed class QuestionEntry
        {
            public string Id { get; set; } = "";
            public string Type { get; set; } = "";
            public string TypeLabel { get; set; } = "";
            public string Prompt { get; set; } = "";
            public List<OptionEntry> Options { get; set; } = new();
            [JsonPropertyName("answerText")]
            public string ResultText { get; set; } = "";

            [JsonPropertyName("answerKeys")]
            public List<string> ResultKeys { get; set; } = new();
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
                    return !string.IsNullOrWhiteSpace(ResultText) ? ResultText.Trim() : (Explanation ?? "").Trim();
                }

                if (Type.Equals("judge", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(ResultText)) return ResultText.Trim();
                    return string.Join("", ResultKeys).Trim();
                }

                if ((Type.Equals("single", StringComparison.OrdinalIgnoreCase) || Type.Equals("multiple", StringComparison.OrdinalIgnoreCase))
                    && ResultKeys.Count > 0)
                {
                    string remapped = RemapChoiceAnswerToCurrentOptions(currentOptions);
                    if (!string.IsNullOrWhiteSpace(remapped)) return remapped;
                    return string.Join("", ResultKeys).Trim();
                }

                return (ResultText ?? "").Trim();
            }

            private string RemapChoiceAnswerToCurrentOptions(IReadOnlyList<LocalSearchOption>? currentOptions)
            {
                if (currentOptions == null || currentOptions.Count == 0 || Options.Count == 0 || ResultKeys.Count == 0)
                {
                    return "";
                }

                const double minimumScore = 0.55;
                var candidateSets = new List<List<(string Key, double Score)>>();
                foreach (string answerKey in ResultKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var bankOption = Options.FirstOrDefault(o => o.Key.Equals(answerKey, StringComparison.OrdinalIgnoreCase));
                    if (bankOption == null || string.IsNullOrWhiteSpace(bankOption.Text)) return "";

                    string bankText = CleanOptionText(bankOption.Text);
                    var candidates = currentOptions
                        .Where(current => !string.IsNullOrWhiteSpace(current.Key) && !string.IsNullOrWhiteSpace(current.Text))
                        .Select(current => (Key: current.Key.Trim().ToUpperInvariant(), Score: OptionSimilarity(bankText, CleanOptionText(current.Text))))
                        .Where(candidate => candidate.Score >= minimumScore)
                        .OrderByDescending(candidate => candidate.Score)
                        .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
                        .ToList();
                    if (candidates.Count == 0) return "";
                    candidateSets.Add(candidates);
                }

                List<string>? bestAssignment = null;
                double bestScore = double.NegativeInfinity;

                void FindBestAssignment(int answerIndex, HashSet<string> usedKeys, List<string> assignedKeys, double totalScore)
                {
                    if (answerIndex == candidateSets.Count)
                    {
                        if (totalScore > bestScore)
                        {
                            bestScore = totalScore;
                            bestAssignment = assignedKeys.ToList();
                        }
                        return;
                    }

                    foreach (var candidate in candidateSets[answerIndex])
                    {
                        if (!usedKeys.Add(candidate.Key)) continue;
                        assignedKeys.Add(candidate.Key);
                        FindBestAssignment(answerIndex + 1, usedKeys, assignedKeys, totalScore + candidate.Score);
                        assignedKeys.RemoveAt(assignedKeys.Count - 1);
                        usedKeys.Remove(candidate.Key);
                    }
                }

                FindBestAssignment(0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new List<string>(), 0);
                return bestAssignment == null
                    ? ""
                    : string.Concat(bestAssignment.OrderBy(key => key, StringComparer.Ordinal));
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
        string Output,
        double Similarity);

    internal sealed record LocalSearchOption(string Key, string Text);
}
