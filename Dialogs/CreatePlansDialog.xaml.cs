using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CadToolsApp.Models;
using CadToolsApp.Services;

namespace CadToolsApp.Dialogs
{
    // One editable row per classified cable. Plain mutable class (no INotifyPropertyChanged) —
    // the DataGrid writes edits straight back into these objects, and ConfirmedRows is only read
    // once, after the grid closes, so no live recompute is needed.
    public class CableReviewRow
    {
        public bool Include { get; set; }
        public string Handle { get; init; } = "";
        public double Length { get; init; }
        public string? Type { get; set; }
        public string? Stripe { get; set; }
        public string? Method { get; set; }
        public string RouteGroupId { get; init; } = "";
        public string Evidence { get; init; } = "";
        public bool IsExisting { get; init; }
        public bool NearBoundary { get; init; }
        public string ExistingFlag => IsExisting ? "yes" : "";
        public string BoundaryFlag => NearBoundary ? "yes" : "";
    }

    // The single confirmation step for Create Plans: the measured cable rows on one tab, and the
    // sheet facts no geometry can supply on the other. Both are needed before anything is written,
    // so they share one OK button rather than two sequential dialogs.
    public partial class CreatePlansDialog : Window
    {
        private static readonly string?[] TypeOptions = { null, "DB7", "DB2", "HDPE", "COAX" };
        private static readonly string?[] StripeOptions = { null, "GREY", "ORANGE", "GREEN" };
        private static readonly string?[] MethodOptions =
        {
            null, "SLEUF", "DOORSTEEK", "HANDBORING", "LIJNBORING",
            "MANTEL110", "MANTEL125", "MANTEL200", "DROOGTREK"
        };

        private static readonly int[] ScaleOptions = { 200, 250, 500, 1000, 2000 };
        private static readonly int[] DepthOptions = { 60, 80 };

        private readonly ObservableCollection<CableReviewRow> _rows = new();

        public List<CableReviewRow> ConfirmedRows => _rows.Where(r => r.Include).ToList();
        public SheetInfo Info { get; }

        public CreatePlansDialog(CableClassifier.ClassifyResult result, SheetInfo info)
        {
            InitializeComponent();
            Info = info;

            colType.ItemsSource = TypeOptions;
            colStripe.ItemsSource = StripeOptions;
            colMethod.ItemsSource = MethodOptions;

            cmbScale.ItemsSource = ScaleOptions.Select(s => $"1/{s}").ToList();
            cmbScale.SelectedIndex = System.Array.IndexOf(ScaleOptions, info.ScaleDenominator) is var i && i >= 0 ? i : 2;
            cmbDepth.ItemsSource = DepthOptions.Select(d => $"{d} cm").ToList();
            cmbDepth.SelectedIndex = System.Array.IndexOf(DepthOptions, info.TrenchDepthCm) is var d2 && d2 >= 0 ? d2 : 1;

            // The classifier's pit count is a starting point the drafter confirms — see
            // CableClassifier.CountNewPits for why callouts, not symbols, are the evidence.
            if (info.PitCount == 0) info.PitCount = result.DetectedPitCount;

            LoadFields(info);

            foreach (var c in result.Cables)
            {
                _rows.Add(new CableReviewRow
                {
                    Include = !c.IsExisting,
                    Handle = c.Handle,
                    Length = c.Length,
                    Type = c.BaseType,
                    Stripe = c.Stripe,
                    Method = c.Method == CableClassifier.MixedMethod ? null : c.Method,
                    RouteGroupId = c.RouteGroupId,
                    Evidence = c.Method == CableClassifier.MixedMethod
                        ? $"[MIXED METHODS — pick one below, or edit the DXF cell directly after saving] {c.Evidence}"
                        : c.Evidence,
                    IsExisting = c.IsExisting,
                    NearBoundary = c.NearScopeBoundary,
                });
            }
            grid.ItemsSource = _rows;

            foreach (var line in result.Log) lstLog.Items.Add(line);

            int flagged = _rows.Count(r => r.NearBoundary) + _rows.Count(r => r.Type == null);
            lblSummary.Text = $"{_rows.Count} cable(s), {flagged} need a look";
        }

        private void LoadFields(SheetInfo info)
        {
            txtDossier.Text    = info.Dossiernummer;
            txtStreet.Text     = info.Street;
            txtCity.Text       = info.City;
            txtOpgemaakt.Text  = info.Opgemaakt;
            txtGetekend.Text   = info.GetekendDoor;
            txtNagekeken.Text  = info.Nagekeken;
            txtUitgevoerd.Text = info.UitgevoerdDoor;
            txtBegin.Text      = info.BeginDerWerken;
            txtEinde.Text      = info.EindeDerWerken;
            txtGeplaatste.Text = info.GeplaatsteLengte;
            txtGesloopte.Text  = info.GesloopteLengte;
            txtPits.Text       = info.PitCount.ToString();
            chkMap.IsChecked   = info.FetchLiggingMap;
        }

        private void SaveFields()
        {
            Info.Dossiernummer    = txtDossier.Text.Trim();
            Info.Street           = txtStreet.Text.Trim();
            Info.City             = txtCity.Text.Trim();
            Info.Opgemaakt        = txtOpgemaakt.Text.Trim();
            Info.GetekendDoor     = txtGetekend.Text.Trim();
            Info.Nagekeken        = txtNagekeken.Text.Trim();
            Info.UitgevoerdDoor   = txtUitgevoerd.Text.Trim();
            Info.BeginDerWerken   = txtBegin.Text.Trim();
            Info.EindeDerWerken   = txtEinde.Text.Trim();
            Info.GeplaatsteLengte = txtGeplaatste.Text.Trim();
            Info.GesloopteLengte  = txtGesloopte.Text.Trim();
            Info.FetchLiggingMap  = chkMap.IsChecked == true;

            Info.PitCount = int.TryParse(txtPits.Text.Trim(), out int pits) && pits >= 0 ? pits : 0;
            if (cmbScale.SelectedIndex >= 0) Info.ScaleDenominator = ScaleOptions[cmbScale.SelectedIndex];
            if (cmbDepth.SelectedIndex >= 0) Info.TrenchDepthCm = DepthOptions[cmbDepth.SelectedIndex];
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            SaveFields();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
