using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CadToolsApp.Dialogs;
using CadToolsApp.Models;
using CadToolsApp.Services;
using Microsoft.Win32;
using netDxf;

namespace CadToolsApp
{
    public partial class MainWindow : Window
    {
        private DxfDocument? _doc;
        private string        _filePath = "";
        private readonly DxfService _dxf = new();

        private static readonly string CableMapCfgPath =
            Path.Combine(AppContext.BaseDirectory, "cable_map.txt");

        private static readonly string PlansLayoutTemplatePath =
            Path.Combine(AppContext.BaseDirectory, "Templates", "table_layout_template.dxf");
        private static readonly string XsecPresetsTemplatePath =
            Path.Combine(AppContext.BaseDirectory, "Templates", "Symboles - Copie.dxf");

        public MainWindow()
        {
            InitializeComponent();
        }

        // ── Open DXF ──────────────────────────────────────────────────────────
        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title  = "Open DXF file",
                Filter = "DXF Drawing (*.dxf)|*.dxf|All files (*.*)|*.*",
            };
            if (ofd.ShowDialog() != true) return;

            try
            {
                _doc      = _dxf.Load(ofd.FileName);
                _filePath = ofd.FileName;
                txtFilePath.Text = _filePath;
                EnableButtons(true);
                SetStatus($"Loaded: {Path.GetFileName(_filePath)}");
                Log($"File loaded: {_filePath}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load DXF:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Save DXF ──────────────────────────────────────────────────────────
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;

            // Save via netDxf to a temp file, then graft any new layout content
            // back into the ORIGINAL file so MultiLeader entities are preserved.
            string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dxf");
            try
            {
                _dxf.Save(_doc, tempPath);
                string mergeMsg = MergeIntoOriginal(_filePath, tempPath, _filePath);
                Log(mergeMsg);
                Log($"Saved: {_filePath}");
                SetStatus("Saved.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        // ── Cross Section ─────────────────────────────────────────────────────
        private void CrossSection_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;

            if (!File.Exists(XsecPresetsTemplatePath))
            {
                MessageBox.Show($"Preset template not found:\n{XsecPresetsTemplatePath}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var presetNames = _dxf.GetPresetNames(XsecPresetsTemplatePath);
            if (presetNames.Count == 0)
            {
                MessageBox.Show("No preset layouts found in the template file.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string folder  = Path.GetDirectoryName(_filePath)!;
            var sections   = _dxf.FindSectionPoints(_doc, folder);
            Log($"Found {sections.Count} section point(s).");

            var dlg = new CrossSectionDialog(sections, folder, presetNames) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Queue.Count == 0) return;

            string fileAddress = ExtractAddressFromFileName(_filePath);
            string fileDossier = ExtractDossierFromFileName(_filePath);
            var cableLengths   = BuildCableLengthMap();

            int appended = 0;
            foreach (var entry in dlg.Queue)
            {
                try
                {
                    var (ok, log) = _dxf.AppendPresetLayout(
                        _doc, XsecPresetsTemplatePath,
                        entry.PresetName, entry.SectionId,
                        string.IsNullOrEmpty(entry.ImagePath) ? null : entry.ImagePath,
                        fileAddress, fileDossier,
                        entry.ModelX, entry.ModelY, cableLengths);

                    foreach (var line in log) Log(line);
                    if (ok) appended++;
                }
                catch (Exception ex)
                {
                    Log($"Error appending '{entry.SectionId}': {ex.Message}");
                }
            }

            if (appended > 0)
            {
                btnSave.IsEnabled = true;
                SetStatus($"{appended} layout(s) appended — click Save to write the file.");
            }
            else
            {
                SetStatus("Cross-section: nothing appended — see the log.");
            }
        }

        // ── Layer Cleanup ─────────────────────────────────────────────────────
        private void Cleanup_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;

            var result = MessageBox.Show(
                "This will turn off target layers (ADN, ADT, HOT, WPI*, WRI*, GVP*…) " +
                "and grey all remaining entities.\n\nContinue?",
                "Layer Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                List<string> off = _dxf.ApplyCleanup(_doc);
                foreach (string n in off) Log($"  Turned off: {n}");
                Log($"Done — {off.Count} layer(s) turned off.");
                SetStatus("Cleanup done — click Save to write the file.");
                btnSave.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cleanup error:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Create Plans ──────────────────────────────────────────────────────
        // Sheet, quantity table, title block and locator map are one deliverable, so they are one
        // action with one review step — the previous split let a sheet be saved with an unfilled
        // table, or a table filled with no sheet to print it on.
        private void CreatePlans_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;

            if (!File.Exists(PlansLayoutTemplatePath))
            {
                MessageBox.Show($"Layout template not found:\n{PlansLayoutTemplatePath}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetStatus("Scanning cables…");
            try
            {
                var classified = _dxf.ClassifyCables(_doc, _filePath, CableMapCfgPath);
                foreach (var line in classified.Log) Log(line);

                if (classified.Cables.Count == 0)
                {
                    SetStatus("Create Plans — no cables found; nothing to quantify.");
                    return;
                }

                var info = SheetInfo.FromFileName(_filePath);
                var dlg = new CreatePlansDialog(classified, info) { Owner = this };
                if (dlg.ShowDialog() != true)
                {
                    SetStatus("Create Plans cancelled.");
                    return;
                }

                var confirmed = dlg.ConfirmedRows.Select(r => new ClassifiedCable(
                    r.Handle, r.Length, r.Type, r.Stripe, r.Method, r.RouteGroupId,
                    r.Evidence, false, false)).ToList();

                SetStatus("Creating plans…");
                var (ok, log) = _dxf.CreatePlans(_doc, PlansLayoutTemplatePath, confirmed, dlg.Info, _filePath);
                foreach (var line in log) Log(line);

                if (ok)
                {
                    SetStatus($"Plans created at {dlg.Info.ScaleLabel} — click Save to write the file.");
                    btnSave.IsEnabled = true;
                }
                else
                {
                    SetStatus("Create Plans — nothing created; see the log.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Create Plans error:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void EnableButtons(bool on)
        {
            btnCrossSection.IsEnabled = on && _doc != null;
            btnCleanup.IsEnabled     = on && _doc != null;
            btnCreatePlans.IsEnabled = on && _doc != null;
            btnSave.IsEnabled        = on && _doc != null;
        }

        // Extracts and cleans the address from the DXF file name.
        // "SW 25241204 - Wouwerstraat 3, 2220 Heist-Op-Den-Berg.dxf" → "Wouwerstraat, Heist-Op-Den-Berg"
        // "AAN_20260630_25246058 Emblem, Oostmalsesteenweg 75.dxf"    → "Emblem, Oostmalsesteenweg"
        private static string ExtractAddressFromFileName(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);

            // Isolate the raw address portion after the project code
            string raw;
            int dashIdx = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx >= 0)
            {
                raw = name[(dashIdx + 3)..].Trim();
            }
            else
            {
                var m = System.Text.RegularExpressions.Regex.Match(name, @"[\d_]+\s+(.+)$");
                raw = m.Success ? m.Groups[1].Value.Trim() : "";
            }

            if (string.IsNullOrEmpty(raw)) return "";

            // Clean each comma-separated part:
            //   strip leading 4-digit postal code  (e.g. "2220 Heist-Op-Den-Berg" → "Heist-Op-Den-Berg")
            //   strip trailing house number         (e.g. "Wouwerstraat 3"         → "Wouwerstraat")
            var parts = raw.Split(',');
            var cleaned = new System.Collections.Generic.List<string>();
            foreach (var part in parts)
            {
                string p = part.Trim();
                p = System.Text.RegularExpressions.Regex.Replace(p, @"^\d{4}\s+", "");
                p = System.Text.RegularExpressions.Regex.Replace(p, @"\s+\d+\w*$", "");
                p = p.Trim();
                if (!string.IsNullOrEmpty(p)) cleaned.Add(p);
            }
            return string.Join(", ", cleaned);
        }

        // Classifies cables in the open DXF and returns total length per base type.
        // Keys match the placeholder names used in the cross-section template: DB7, DB2, HDPE, COAX, EXISTING.
        private Dictionary<string, double> BuildCableLengthMap()
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (_doc == null) return map;
            try
            {
                var classified = _dxf.ClassifyCables(_doc, _filePath, CableMapCfgPath);
                foreach (var line in classified.Log) Log(line);
                // Group by (BaseType, RouteGroupId) so parallel cables in the same trench
                // (e.g. DB7 grey + DB7 orange) count as one route length, not doubled.
                var routeGroups = classified.Cables
                    .Where(c => !string.IsNullOrEmpty(c.BaseType))
                    .GroupBy(c => (BaseType: c.BaseType!, RouteId: c.RouteGroupId));

                foreach (var grp in routeGroups)
                {
                    double routeLen = grp.Max(c => c.Length);
                    string key = grp.Key.BaseType;
                    map[key] = map.TryGetValue(key, out double prev) ? prev + routeLen : routeLen;
                }
                if (map.Count > 0)
                    Log("Cable lengths: " + string.Join(", ", map.Select(kv => $"{kv.Key}={kv.Value:F1}m")));
            }
            catch (Exception ex)
            {
                Log($"Cable classification skipped: {ex.Message}");
            }
            return map;
        }

        // Extracts the dossier number + raw address for the Dossiernummer field.
        // "SW 25241204 - Wouwerstraat 3, 2220 Heist-Op-Den-Berg.dxf" → "25241204 - Wouwerstraat 3, 2220 Heist-Op-Den-Berg"
        // "AAN_20260630_25246058 Emblem, Oostmalsesteenweg 75.dxf"    → "25246058 Emblem, Oostmalsesteenweg 75"
        private static string ExtractDossierFromFileName(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);

            // Pattern 1: "SW 25241204 - ..." → strip the first word prefix ("SW ")
            int dashIdx = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx >= 0)
            {
                int firstSpace = name.IndexOf(' ');
                if (firstSpace >= 0) return name[(firstSpace + 1)..].Trim();
            }

            // Pattern 2: "AAN_20260630_25246058 ..." → strip up to and including last underscore
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore >= 0) return name[(lastUnderscore + 1)..].Trim();

            return name;
        }

        // ── DXF MultiLeader preservation ─────────────────────────────────────
        //
        // Strategy: the temp file netDxf produces IS a valid DXF (layouts intact).
        // We only need to restore what netDxf silently dropped: MULTILEADER entities,
        // MLEADERSTYLE objects, and the ACAD_MLEADERSTYLE dictionary.
        private static string MergeIntoOriginal(string origPath, string tempPath, string outputPath)
        {
            string[] orig = DxfReadLines(origPath);
            string[] temp = DxfReadLines(tempPath);
            bool crlf = File.ReadAllBytes(origPath).AsSpan().IndexOf((byte)'\r') >= 0;

            // Extract everything MultiLeader-related from the original file.
            var mlEntities   = DxfExtractModelSpaceEntities(orig, "MULTILEADER");
            var mlStyles     = DxfExtractObjectsByType(orig, "MLEADERSTYLE");
            var (mlDictLines, mlDictHandle) = DxfExtractNamedDict(orig, "ACAD_MLEADERSTYLE");

            var diag = $"ML={mlEntities.Count} Styles={mlStyles.Count} Dict={(mlDictLines.Count > 0 ? mlDictHandle : "none")}";

            if (mlEntities.Count == 0 && mlStyles.Count == 0)
            {
                // Nothing to restore — just copy the temp file as-is.
                if (outputPath != tempPath) File.Copy(tempPath, outputPath, overwrite: true);
                return $"save: no MultiLeaders found in original. {diag}";
            }

            // Start from the valid netDxf output (temp).
            var result = temp.ToList();

            // Insert order: objects first (higher indices), then *Model_Space entities.
            // Find positions on the current result before any modification.

            // 3. OBJECTS section — inject styles + ACAD_MLEADERSTYLE dict before ENDSEC.
            var objsToInject = new List<List<string>>(mlStyles);
            if (mlDictLines.Count > 0) objsToInject.Add(mlDictLines);
            if (objsToInject.Count > 0)
            {
                int objEndsec = DxfFindSectionEndsec(result, "OBJECTS");
                if (objEndsec >= 0)
                    result.InsertRange(objEndsec, objsToInject.SelectMany(o => o));
            }

            // 4. Root DICTIONARY — add ACAD_MLEADERSTYLE → handle entry if missing.
            if (mlDictHandle != null)
                DxfAddRootDictEntry(result, "ACAD_MLEADERSTYLE", mlDictHandle);

            // 1. *Model_Space block — inject MULTILEADER entities before ENDBLK.
            if (mlEntities.Count > 0)
            {
                int msEndblk = DxfFindModelSpaceEndblk(result);
                if (msEndblk >= 0)
                    result.InsertRange(msEndblk, mlEntities.SelectMany(e => e));
            }

            // Write result.
            string joined = string.Join("\n", result);
            if (crlf) joined = joined.Replace("\n", "\r\n");
            using var fw = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var sw = new StreamWriter(fw, Encoding.Latin1);
            sw.Write(joined);

            return $"merge OK — {diag}";
        }

        private static string[] DxfReadLines(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.Latin1);
            return sr.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }

        // Extracts entities of a given type from the *Model_Space block.
        private static List<List<string>> DxfExtractModelSpaceEntities(string[] lines, string entityType)
        {
            var result = new List<List<string>>();
            bool inModelSpace = false;
            int i = 0;
            while (i + 1 < lines.Length)
            {
                string code = lines[i].Trim(), val = lines[i + 1].Trim();
                if (code == "0" && (val == "BLOCK" || val == "ENDSEC")) { inModelSpace = false; i += 2; continue; }
                if (code == "2" && val.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase)) { inModelSpace = true; i += 2; continue; }
                if (inModelSpace && code == "0" && val == "ENDBLK") break;
                if (!inModelSpace || code != "0" || val != entityType) { i += 2; continue; }

                var entity = new List<string> { lines[i], lines[i + 1] };
                int k = i + 2;
                while (k + 1 < lines.Length && lines[k].Trim() != "0")
                { entity.Add(lines[k]); entity.Add(lines[k + 1]); k += 2; }
                result.Add(entity);
                i = k;
            }
            return result;
        }

        // Extracts all objects of the given type from the OBJECTS section.
        private static List<List<string>> DxfExtractObjectsByType(string[] lines, string objectType)
        {
            var result = new List<List<string>>();
            bool inObjects = false;
            int i = 0;
            while (i + 1 < lines.Length)
            {
                string code = lines[i].Trim(), val = lines[i + 1].Trim();
                if (code == "0" && val == "SECTION") { inObjects = false; i += 2; continue; }
                if (code == "2" && val == "OBJECTS")  { inObjects = true;  i += 2; continue; }
                if (!inObjects) { i += 2; continue; }
                if (code == "0" && val == "ENDSEC") break;
                if (code != "0" || val != objectType) { i += 2; continue; }

                var obj = new List<string> { lines[i], lines[i + 1] };
                int k = i + 2;
                while (k + 1 < lines.Length && lines[k].Trim() != "0")
                { obj.Add(lines[k]); obj.Add(lines[k + 1]); k += 2; }
                result.Add(obj);
                i = k;
            }
            return result;
        }

        // Finds the handle of a named entry in the root DICTIONARY, then extracts
        // the DICTIONARY object with that handle from the OBJECTS section.
        private static (List<string> Lines, string? Handle) DxfExtractNamedDict(string[] lines, string dictName)
        {
            string? handle = null;
            for (int i = 0; i + 3 < lines.Length; i++)
            {
                if (lines[i].Trim() == "3" && lines[i + 1].Trim() == dictName &&
                    lines[i + 2].Trim() == "350")
                { handle = lines[i + 3].Trim(); break; }
            }
            if (handle == null) return (new List<string>(), null);

            for (int i = 0; i + 3 < lines.Length; i++)
            {
                if (lines[i].Trim() != "0" || lines[i + 1].Trim() != "DICTIONARY") continue;
                if (lines[i + 2].Trim() != "5" ||
                    !lines[i + 3].Trim().Equals(handle, StringComparison.OrdinalIgnoreCase)) continue;
                var dictLines = new List<string> { lines[i], lines[i + 1] };
                int k = i + 2;
                while (k + 1 < lines.Length && lines[k].Trim() != "0")
                { dictLines.Add(lines[k]); dictLines.Add(lines[k + 1]); k += 2; }
                return (dictLines, handle);
            }
            return (new List<string>(), handle);
        }

        // Returns the index of the ENDBLK "  0" line inside the *Model_Space block.
        private static int DxfFindModelSpaceEndblk(List<string> lines)
        {
            bool inModelSpace = false;
            for (int i = 0; i + 1 < lines.Count; i += 2)
            {
                string code = lines[i].Trim(), val = lines[i + 1].Trim();
                if (inModelSpace && code == "0" && val == "ENDBLK") return i;
                if (code == "0" && (val == "BLOCK" || val == "ENDBLK" || val == "ENDSEC")) inModelSpace = false;
                if (code == "2" && val.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase)) inModelSpace = true;
            }
            return -1;
        }

        // Adds a 3/350 entry to the root named-object DICTIONARY if not already present.
        private static void DxfAddRootDictEntry(List<string> lines, string name, string handle)
        {
            bool inObjects = false;
            for (int i = 0; i + 1 < lines.Count; i += 2)
            {
                string code = lines[i].Trim(), val = lines[i + 1].Trim();
                if (code == "0" && val == "SECTION") inObjects = false;
                if (code == "2" && val == "OBJECTS")  inObjects = true;
                if (!inObjects) continue;
                if (code == "0" && val == "ENDSEC") break;
                if (code != "0" || val != "DICTIONARY") continue;

                // Verify this is the root dict (owner = "0").
                bool isRoot = false;
                for (int j = i + 2; j + 1 < lines.Count && lines[j].Trim() != "0"; j += 2)
                    if (lines[j].Trim() == "330" && lines[j + 1].Trim() == "0") { isRoot = true; break; }
                if (!isRoot) continue;

                // Check entry not already present.
                for (int j = i + 2; j + 1 < lines.Count && lines[j].Trim() != "0"; j += 2)
                    if (lines[j].Trim() == "3" && lines[j + 1].Trim() == name) return;

                // Insert before end of this DICTIONARY.
                int end = i + 2;
                while (end < lines.Count && lines[end].Trim() != "0") end++;
                lines.InsertRange(end, new[] { "  3", name, "350", handle });
                return;
            }
        }

        // Index of the ENDSEC line for the named SECTION (pair-steps from start).
        private static int DxfFindSectionEndsec(List<string> lines, string sectionName)
        {
            bool inSection = false;
            for (int i = 0; i + 1 < lines.Count; i += 2)
            {
                string code = lines[i].Trim(), val = lines[i + 1].Trim();
                if (code == "0" && val == "SECTION")   inSection = false;
                if (code == "2" && val == sectionName) inSection = true;
                if (inSection && code == "0" && val == "ENDSEC") return i;
            }
            return -1;
        }

        private void Log(string msg)
        {
            txtLog.AppendText(msg + "\n");
            scroll.ScrollToBottom();
        }

        private void SetStatus(string msg) => lblStatus.Text = msg;
    }
}
