using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Two unrelated layout templates now: the sheet/quantity-table one used by Create Plans,
        // and the cross-section pair (frame + icon blocks) used by Cross Section.
        private static readonly string PlansLayoutTemplatePath =
            Path.Combine(AppContext.BaseDirectory, "Templates", "table_layout_template.dxf");
        private static readonly string XsecLayoutTemplatePath =
            Path.Combine(AppContext.BaseDirectory, "Templates", "xsec_layout.dxf");
        private static readonly string XsecIconsTemplatePath =
            Path.Combine(AppContext.BaseDirectory, "Templates", "xsec_icons.dxf");

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

        // ── Cross Section ─────────────────────────────────────────────────────
        private void CrossSection_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;

            string folder = Path.GetDirectoryName(_filePath)!;
            var sections  = _dxf.FindSectionPoints(_doc, folder);
            Log($"Found {sections.Count} section point(s).");

            var dlg = new CrossSectionDialog(sections, folder) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            try
            {
                _dxf.DrawCrossSection(_doc, dlg.SectionId, dlg.Zones,
                    dlg.InsertX, dlg.InsertY, XsecLayoutTemplatePath, XsecIconsTemplatePath,
                    dlg.SelectedImagePath);

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

        private void Log(string msg)
        {
            txtLog.AppendText(msg + "\n");
            scroll.ScrollToBottom();
        }

        private void SetStatus(string msg) => lblStatus.Text = msg;
    }
}
