using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace JLChnToZ.VRC.VVMW.Editors {
    /// <summary>
    /// Checks that the loudness fork is installed and that the playlist gains in the open scenes will apply.
    /// Menu: Tools/VizVid/Loudness Doctor. Scripts: <c>LoudnessDoctor.Run()</c> returns the report as JSON.
    /// </summary>
    public static class LoudnessDoctor {
        const string PackageName = "idv.jlchntoz.vvmw";
        const string Pass = "pass", Warn = "warn", Fail = "fail";

        [Serializable]
        public class Check {
            public string status;
            public string id;
            public string message;
        }

        [Serializable]
        public class Report {
            public string result; // worst status: pass, warn or fail
            public string version;
            public string summary;
            public List<Check> checks = new List<Check>();
        }

        [MenuItem("Tools/VizVid/Loudness Doctor")]
        static void ShowMenu() {
            var report = Analyze();
            var sb = new StringBuilder();
            foreach (var c in report.checks) sb.Append('[').Append(c.status.ToUpperInvariant()).Append("] ").AppendLine(c.message);
            Debug.Log("[VizVid] Loudness Doctor: " + report.result.ToUpperInvariant() + "\n" + sb);
            EditorUtility.DisplayDialog("VizVid Loudness Doctor: " + report.result.ToUpperInvariant(), report.summary + "\n\nFull report is in the Console.", "OK");
        }

        /// <summary>Runs all checks and returns the report as JSON (for uab call).</summary>
        public static string Run() => JsonUtility.ToJson(Analyze(), true);

        public static Report Analyze() {
            var r = new Report();
            void Add(string status, string id, string message) => r.checks.Add(new Check { status = status, id = id, message = message });

            // 1. Which package is this?
            var info = PackageManagerPackageInfo.FindForAssembly(typeof(FrontendHandler).Assembly);
            r.version = info != null ? info.version : "unknown";
            bool hasGainField = typeof(FrontendHandler).GetField("playListEntryGains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;
            if (info == null) Add(Fail, "package", "VizVid is not installed as a package, so the fork cannot be identified. Install it from the fork's VPM listing.");
            else if (!hasGainField) Add(Fail, "package", "VizVid " + info.version + " is installed, but it is not the loudness fork: it has no gain field. Gains in your playlist JSON will be ignored.");
            else if (!r.version.Contains("-loudness.")) Add(Warn, "package", "The gain code is present, but the version is " + r.version + ", which does not look like a loudness fork release. If this is an upstream build, check that it really includes the fork's changes.");
            else Add(Pass, "package", "The loudness fork is installed: VizVid " + r.version + " (" + DescribeSource(info) + ").");

            // 2. Only one copy?
            var copies = FindVizVidScriptCopies(info);
            if (copies.Count == 0) Add(Pass, "single-copy", "This is the only VizVid copy in the project.");
            else Add(Fail, "single-copy", "Another VizVid copy exists, so scripts may resolve to the wrong one: " + string.Join(", ", copies.Take(3)) + (copies.Count > 3 ? ", ..." : "") + ". Remove the extra copy.");

            // 3. Scenes
            var scenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++) {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) scenes.Add(s);
            }
            if (EditorApplication.isPlaying) Add(Warn, "play-mode", "The editor is in play mode. Changes made now are discarded when you stop. Exit play mode, then run the Doctor again.");
            if (scenes.Count == 0) Add(Warn, "scene", "No scene is open. Open your world scene and run the Doctor again.");
            foreach (var s in scenes.Where(s => s.isDirty))
                Add(Warn, "unsaved", "Scene \"" + s.name + "\" has unsaved changes. Gains that are not saved to the scene will not be in your build. Press Ctrl+S, then run the Doctor again. (Unity can also mark a scene changed just by opening it.)");
            var dirtyEditor = PlayListEditorWindow.UnsavedEditorHandlerName();
            if (dirtyEditor != null)
                Add(Warn, "editor-unsaved", "The playlist editor has changes for \"" + dirtyEditor + "\" that are not saved to the player. Press Save in the playlist editor window, then save the scene.");

            var handlers = Resources.FindObjectsOfTypeAll<FrontendHandler>()
                .Where(h => h != null && h.gameObject.scene.IsValid() && h.gameObject.scene.isLoaded && !EditorUtility.IsPersistent(h))
                .OrderBy(h => h.gameObject.scene.name).ThenBy(h => GetPath(h.transform)).ToList();
            var cores = Resources.FindObjectsOfTypeAll<Core>()
                .Where(c => c != null && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded && !EditorUtility.IsPersistent(c)).ToList();
            if (scenes.Count > 0 && handlers.Count == 0) Add(Fail, "frontend", "No VizVid player (FrontendHandler) is in the open scenes. Add one from GameObject > VizVid.");
            if (cores.Count > 1) Add(Warn, "cores", cores.Count + " VizVid Core objects are in the scene. Each Frontend only drives its own Core, so check that each player has its own playlist and gains.");

            int withPlaylists = 0;
            foreach (var h in handlers) {
                var name = "\"" + GetPath(h.transform) + "\"";
                using (var so = new SerializedObject(h)) {
                    var titles = so.FindProperty("playListTitles");
                    if (titles == null) { Add(Fail, "playlist", name + ": the playlist fields are missing, so this Frontend is not from the fork."); continue; }
                    int plCount = titles.arraySize;
                    var coreRef = so.FindProperty("core").objectReferenceValue;
                    if (coreRef == null) Add(Fail, "core", name + ": no Core is assigned, so this player cannot play or apply gains.");
                    else CheckCoreAudio(coreRef as Core, name, Add);
                    if (!h.gameObject.activeInHierarchy) Add(Warn, "inactive", name + " is inactive in the scene. It only runs if something enables it at runtime.");
                    if (plCount == 0) { Add(Pass, "playlist", name + ": no playlists, so there is nothing to attenuate."); continue; }
                    withPlaylists++;
                    var offsets = so.FindProperty("playListUrlOffsets");
                    var urls = so.FindProperty("playListUrls");
                    var gains = so.FindProperty("playListEntryGains");
                    var players = so.FindProperty("playListPlayerIndex");
                    int total = urls.arraySize;
                    for (int i = 0; i < plCount; i++) {
                        int start = offsets.GetArrayElementAtIndex(i).intValue;
                        int end = i < plCount - 1 ? offsets.GetArrayElementAtIndex(i + 1).intValue : total;
                        int n = end - start;
                        string pl = name + " playlist \"" + titles.GetArrayElementAtIndex(i).stringValue + "\": ";
                        if (n <= 0) { Add(Pass, "playlist", pl + "0 entries."); continue; }
                        if (gains == null || gains.arraySize == 0) {
                            Add(Warn, "gains", pl + n + " entries, but the gain array is empty. Every entry plays at full volume. Import your JSON with gain fields, or bake loudness, then save.");
                            continue;
                        }
                        if (gains.arraySize < end) {
                            int covered = Mathf.Clamp(gains.arraySize - start, 0, n);
                            Add(Warn, "gains", pl + n + " entries, but the gain array only covers " + covered + " of them (it has " + gains.arraySize + (gains.arraySize == 1 ? " value" : " values") + ", this playlist needs " + end + "). The rest play at full volume.");
                            continue;
                        }
                        int atten = 0, silent = 0, ytUnity = 0, dynamic = 0;
                        float minDb = 0, maxDb = float.NegativeInfinity;
                        for (int j = start; j < end && j < total; j++) {
                            float g = j < gains.arraySize ? gains.GetArrayElementAtIndex(j).floatValue : 1F;
                            g = Mathf.Clamp01(g);
                            string url = urls.GetArrayElementAtIndex(j).FindPropertyRelative("url").stringValue ?? "";
                            if (players != null && j < players.arraySize && players.GetArrayElementAtIndex(j).intValue == 0) dynamic++;
                            if (g < 0.9999F) {
                                atten++;
                                if (g <= 0F) silent++;
                                else {
                                    float db = 20F * Mathf.Log10(g);
                                    minDb = Mathf.Min(minDb, db);
                                    maxDb = Mathf.Max(maxDb, db);
                                }
                            } else if (IsYouTube(url)) ytUnity++;
                        }
                        string line = pl + n + " entries, gain array present; " + atten + " attenuated" +
                            (atten - silent > 0 ? " (" + RangeDb(minDb, maxDb) + ")" : "") + ".";
                        var status = Pass;
                        if (ytUnity > 0) {
                            line += " " + ytUnity + " YouTube " + (ytUnity == 1 ? "entry is" : "entries are") + " at full volume (0 dB): either not baked yet, or already at or below YouTube's target. The file cannot tell these apart; run Bake YouTube loudness to find out.";
                            status = Warn;
                        }
                        if (silent > 0) { line += " " + silent + " entries have gain 0 and will be silent."; status = Warn; }
                        if (dynamic > 0) { line += " " + dynamic + " dynamic-playlist " + (dynamic == 1 ? "entry" : "entries") + " load at runtime and get no gain."; status = Warn; }
                        Add(status, "gains", line);
                    }
                    if (gains != null && gains.arraySize > total)
                        Add(Warn, "gains", name + ": the gain array has " + gains.arraySize + " values for " + total + " entries; the extra values are ignored.");
                }
            }
            if (withPlaylists > 0)
                Add(Pass, "scope", "Gains apply only to videos played from a playlist. Queued videos and pasted URLs always play at full volume.");

            var worst = r.checks.Any(c => c.status == Fail) ? Fail : r.checks.Any(c => c.status == Warn) ? Warn : Pass;
            r.result = worst;
            int nw = r.checks.Count(c => c.status == Warn), nf = r.checks.Count(c => c.status == Fail);
            r.summary = worst == Pass ? "All checks passed. VizVid loudness fork " + r.version + " is installed and the gains in your scene will apply."
                : (nf > 0 ? nf + " problem(s)" : "") + (nf > 0 && nw > 0 ? " and " : "") + (nw > 0 ? nw + " warning(s)" : "") + ". Read the report in the Console.";
            return r;
        }

        static void CheckCoreAudio(Core core, string name, Action<string, string, string> add) {
            if (core == null) return;
            using (var so = new SerializedObject(core)) {
                var sources = so.FindProperty("audioSources");
                var controllers = so.FindProperty("audioControllers");
                int s = sources != null ? Enumerable.Range(0, sources.arraySize).Count(i => sources.GetArrayElementAtIndex(i).objectReferenceValue != null) : 0;
                int c = controllers != null ? Enumerable.Range(0, controllers.arraySize).Count(i => controllers.GetArrayElementAtIndex(i).objectReferenceValue != null) : 0;
                if (s + c == 0) add(Warn, "audio", name + ": its Core has no audio sources or audio controllers, so the gain has nothing to attenuate.");
            }
        }

        static List<string> FindVizVidScriptCopies(PackageManagerPackageInfo info) {
            var result = new List<string>();
            string own = info != null ? info.assetPath : null; // "Packages/idv.jlchntoz.vvmw"
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript FrontendHandler")) {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/FrontendHandler.cs", StringComparison.Ordinal)) continue;
                if (own != null && path.StartsWith(own + "/", StringComparison.Ordinal)) continue;
                result.Add(Path.GetDirectoryName(path).Replace('\\', '/'));
            }
            foreach (var p in PackageManagerPackageInfo.GetAllRegisteredPackages())
                if (p.name == PackageName && (info == null || p.assetPath != info.assetPath)) result.Add(p.assetPath);
            return result.Distinct().ToList();
        }

        static string DescribeSource(PackageManagerPackageInfo info) {
            switch (info.source) {
                case UnityEditor.PackageManager.PackageSource.Embedded: return "embedded in Packages/";
                case UnityEditor.PackageManager.PackageSource.Local: return "local folder";
                default: return info.source.ToString();
            }
        }

        static bool IsYouTube(string url) =>
            url.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 || url.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static string FormatDb(float db) => (db <= -0.05F ? "−" : "") + Mathf.Abs(db).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        internal static string RangeDb(float minDb, float maxDb) {
            string lo = FormatDb(minDb), hi = FormatDb(maxDb);
            return (lo == hi ? lo : lo + " to " + hi) + " dB";
        }

        static string GetPath(Transform t) {
            var sb = new StringBuilder(t.name);
            for (t = t.parent; t != null; t = t.parent) sb.Insert(0, t.name + "/");
            return sb.ToString();
        }
    }
}
