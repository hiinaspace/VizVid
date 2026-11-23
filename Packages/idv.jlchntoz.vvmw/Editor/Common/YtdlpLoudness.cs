using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using UnityEditor;
using Cysharp.Threading.Tasks;

namespace JLChnToZ.VRC.VVMW.Editors {
    /// <summary>
    /// Fetches YouTube's per-video loudness through a yt-dlp build that exposes
    /// <c>loudness_lkfs</c> and <c>loudness_target_lkfs</c> (see the yt-dlp loudness-info branch),
    /// and turns it into a linear gain: 10^(min(0, target - absolute) / 20).
    /// </summary>
    public static class YtdlpLoudness {
        const string LOUDNESS_YTDLP_PREF_KEY = "VVMW_YTDLP_LOUDNESS_PATH";
        const int PROCESS_TIMEOUT_MS = 120000;
        static readonly Regex videoIdPattern = new Regex(
            @"^(?:https?://)?(?:(?:www\.|m\.|music\.)?youtube(?:-nocookie)?\.com/(?:watch\?(?:[^#]*&)?v=|shorts/|live/|embed/|v/)|youtu\.be/)([A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public struct Result {
            public bool ok;
            public string videoId;
            public double absoluteLkfs, targetLkfs;
            public float gain; // Linear; 1 when not ok
            public string error;
        }

        /// <summary>The yt-dlp used for loudness lookups. Falls back to the playlist importer's yt-dlp, which lacks the fields unless patched.</summary>
        public static string LoudnessYtdlpPath {
            get {
                var path = EditorPrefs.GetString(LOUDNESS_YTDLP_PREF_KEY, string.Empty);
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : YtdlpResolver.YtdlpPath;
            }
            set {
                if (string.IsNullOrEmpty(value) || !File.Exists(value)) return;
                EditorPrefs.SetString(LOUDNESS_YTDLP_PREF_KEY, value);
            }
        }

        public static bool TryGetYouTubeId(string url, out string videoId) {
            videoId = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            var m = videoIdPattern.Match(url.Trim());
            if (!m.Success) return false;
            videoId = m.Groups[1].Value;
            return true;
        }

        /// <summary>Attenuation-only policy: quieter-than-target videos stay at unity.</summary>
        public static float ComputeGain(double absoluteLkfs, double targetLkfs) {
            double db = Math.Min(0, targetLkfs - absoluteLkfs);
            return (float)Math.Round(Math.Pow(10, db / 20), 4);
        }

        /// <summary>Parses the `LOUD:abs:target` line printed by yt-dlp (`NA` for absent fields).</summary>
        public static Result ParseOutput(string videoId, string stdout) {
            var result = new Result { videoId = videoId, gain = 1F };
            foreach (var raw in (stdout ?? string.Empty).Split('\n')) {
                var line = raw.Trim();
                if (!line.StartsWith("LOUD:", StringComparison.Ordinal)) continue;
                var parts = line.Split(':');
                if (parts.Length == 3 &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var abs) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var target) &&
                    !double.IsNaN(abs) && !double.IsNaN(target) && !double.IsInfinity(abs) && !double.IsInfinity(target)) {
                    result.ok = true;
                    result.absoluteLkfs = abs;
                    result.targetLkfs = target;
                    result.gain = ComputeGain(abs, target);
                    return result;
                }
                result.error = "no loudness data in yt-dlp output (is this a yt-dlp build with loudness_lkfs?)";
                return result;
            }
            result.error = "yt-dlp printed no loudness line";
            return result;
        }

        static (int exitCode, string stdout, string stderr) RunYtdlp(string path, string videoId, CancellationToken token) {
            var args = "--no-playlist --no-warnings --no-exec --print LOUD:%(loudness_lkfs)s:%(loudness_target_lkfs)s " +
                $"https://www.youtube.com/watch?v={videoId}";
            var startInfo = new ProcessStartInfo(path, args) {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            using var reg = token.Register(() => {
                try { if (!process.HasExited) process.Kill(); } catch { }
            });
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(PROCESS_TIMEOUT_MS)) {
                try { process.Kill(); } catch { }
                return (-1, stdout, "timed out");
            }
            token.ThrowIfCancellationRequested();
            return (process.ExitCode, stdout, stderrTask.Result);
        }

        static string LastLine(string text) {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var lines = text.Trim().Split('\n');
            var line = lines[lines.Length - 1].Trim();
            return line.Length > 300 ? line.Substring(0, 300) : line;
        }

        /// <summary>Looks up one video. One retry on process failure; never throws except for cancellation.</summary>
        public static async UniTask<Result> Fetch(string videoId, string ytdlpPath, CancellationToken token, int retryDelayMs = 3000) {
            var failed = new Result { videoId = videoId, gain = 1F };
            if (string.IsNullOrEmpty(ytdlpPath) || !File.Exists(ytdlpPath)) {
                failed.error = "yt-dlp not found at " + ytdlpPath;
                return failed;
            }
            for (int attempt = 0; attempt < 2; attempt++) {
                if (attempt > 0) await UniTask.Delay(retryDelayMs, cancellationToken: token);
                try {
                    var (exit, stdout, stderr) = await UniTask.RunOnThreadPool(() => RunYtdlp(ytdlpPath, videoId, token), cancellationToken: token);
                    if (exit == 0) return ParseOutput(videoId, stdout); // Absent data won't change on retry
                    failed.error = $"yt-dlp exit {exit}: {LastLine(stderr)}";
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    failed.error = "yt-dlp failed to run: " + ex.Message;
                }
            }
            return failed;
        }

        /// <summary>
        /// Looks up each URL in order, calling <paramref name="onResult"/> as each one finishes.
        /// Non-YouTube URLs yield ok=false with a null videoId; repeats of one video are fetched once.
        /// Sequential, with <paramref name="delayMs"/> between network fetches.
        /// </summary>
        public static async UniTask FetchAll(IList<string> urls, string ytdlpPath, CancellationToken token,
            Action<int, int, string> progress, Action<int, Result> onResult, int delayMs = 1500) {
            var cache = new Dictionary<string, Result>();
            bool fetchedOnce = false;
            for (int i = 0; i < urls.Count; i++) {
                token.ThrowIfCancellationRequested();
                if (!TryGetYouTubeId(urls[i], out var id)) {
                    onResult?.Invoke(i, new Result { gain = 1F, error = "not a YouTube URL" });
                    continue;
                }
                progress?.Invoke(i, urls.Count, id);
                if (!cache.TryGetValue(id, out var result)) {
                    if (fetchedOnce) await UniTask.Delay(delayMs, cancellationToken: token);
                    result = await Fetch(id, ytdlpPath, token);
                    fetchedOnce = true;
                    cache[id] = result;
                }
                onResult?.Invoke(i, result);
            }
        }
    }
}
