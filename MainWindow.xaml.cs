using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

        private static readonly string TemplateCfgPath =
            Path.Combine(AppContext.BaseDirectory, "xsec_template.txt");

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
            var sfd = new SaveFileDialog
            {
                Title    = "Save DXF",
                Filter   = "DXF Drawing (*.dxf)|*.dxf",
                FileName = Path.GetFileName(_filePath),
            };
            if (sfd.ShowDialog() != true) return;
            try
            {
                _dxf.Save(_doc, sfd.FileName);
                _filePath = sfd.FileName;
                txtFilePath.Text = _filePath;
                Log($"Saved: {_filePath}");
                SetStatus("Saved.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Street View ──────────────────────────────────────────────────────
        private async void StreetView_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            EnableButtons(false);
            SetStatus("Fetching street view images…");

            string folder = Path.GetDirectoryName(_filePath)!;
            var sections  = _dxf.FindSectionPoints(_doc, folder);

            if (sections.Count == 0)
            {
                Log("No section points found on 'Cross sections' layer.");
                EnableButtons(true);
                SetStatus("Done — no section points found.");
                return;
            }

            Log($"Found {sections.Count} section point(s). Downloading images…");

            var svc = new StreetViewService();
            svc.Progress += msg => Dispatcher.Invoke(() => Log(msg));

            await Task.Run(() => svc.FetchImages(sections, folder));

            EnableButtons(true);
            SetStatus("Street view done.");
        }

        // ── Cross Section ─────────────────────────────────────────────────────
        private void CrossSection_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;

            string folder = Path.GetDirectoryName(_filePath)!;
            var sections  = _dxf.FindSectionPoints(_doc, folder);
            Log($"Found {sections.Count} section point(s).");

            string templatePath = "";
            try { if (File.Exists(TemplateCfgPath)) templatePath = File.ReadAllText(TemplateCfgPath).Trim(); }
            catch { }

            var dlg = new CrossSectionDialog(sections, templatePath) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            if (!string.IsNullOrEmpty(dlg.TemplatePath))
            {
                try { File.WriteAllText(TemplateCfgPath, dlg.TemplatePath); } catch { }
            }

            try
            {
                _dxf.DrawCrossSection(_doc, dlg.SectionId, dlg.Zones,
                    dlg.InsertX, dlg.InsertY, dlg.TemplatePath);

                Log($"Cross-section '{dlg.SectionId}' drawn at ({dlg.InsertX}, {dlg.InsertY}).");
                SetStatus("Cross-section added — click Save to write the file.");
                btnSave.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error drawing cross-section:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // ── Fill Table ────────────────────────────────────────────────────────
        private void FillTable_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            SetStatus("Scanning cables…");

            try
            {
                var (filled, log) = _dxf.FillTable(_doc);
                Log(log);
                if (filled > 0)
                {
                    SetStatus($"Fill table done — {filled} cell(s) written. Click Save.");
                    btnSave.IsEnabled = true;
                }
                else
                {
                    SetStatus("Fill table — nothing written.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fill table error:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void EnableButtons(bool on)
        {
            btnStreetView.IsEnabled  = on && _doc != null;
            btnCrossSection.IsEnabled = on && _doc != null;
            btnCleanup.IsEnabled     = on && _doc != null;
            btnFillTable.IsEnabled   = on && _doc != null;
            btnSave.IsEnabled        = on && _doc != null;
        }

        private void Log(string msg)
        {
            txtLog.AppendText(msg + "\n");
            scroll.ScrollToBottom();
        }

        private void SetStatus(string msg) => lblStatus.Text = msg;
    }
}
