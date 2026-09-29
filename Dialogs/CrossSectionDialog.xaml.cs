using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CadToolsApp.Models;
using CadToolsApp.Services;

namespace CadToolsApp.Dialogs
{
    public partial class CrossSectionDialog : Window
    {
        private readonly List<string> _presetNames;
        private readonly string       _folder;
        private bool                  _webViewReady;
        private SectionInfo?          _pendingSection;

        public string        SectionId          { get; private set; } = "";
        public string        SelectedPresetName  { get; private set; } = "";
        public SectionInfo?  SelectedSection    { get; private set; }
        public string        SelectedImagePath  => SelectedSection?.ImagePath ?? "";

        public record QueueEntry(string SectionId, string PresetName, string ImagePath, string Address, double ModelX, double ModelY);
        public List<QueueEntry> Queue { get; } = new();

        public CrossSectionDialog(IList<SectionInfo> sections, string folder, List<string> presetNames)
        {
            InitializeComponent();
            _folder      = folder;
            _presetNames = presetNames;

            lstSections.ItemsSource = sections;
            cmbPreset.ItemsSource   = presetNames;

            if (presetNames.Count > 0)
                cmbPreset.SelectedIndex = 0;

            if (sections.Count > 0)
                lstSections.SelectedIndex = 0;

            Loaded += async (s, e) =>
            {
                await InitWebViewAsync();
            };
        }

        // ── Street name lookup ────────────────────────────────────────────────

        private Task FetchStreetNamesAsync(IList<SectionInfo> sections)
        {
            // Geocoding API not enabled — section IDs are shown without street names.
            // The DXF address is extracted from the file name in MainWindow.
            return Task.CompletedTask;
        }

        // ── WebView2 ──────────────────────────────────────────────────────────

        private async Task InitWebViewAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CadToolsApp", "WebView2");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
                _webViewReady    = true;
                btnCapture.IsEnabled = true;
                if (_pendingSection != null) LoadPanorama(_pendingSection);
            }
            catch (Exception ex)
            {
                lblCaptureStatus.Text = $"Street View unavailable: {ex.Message}";
            }
        }

        private void LoadPanorama(SectionInfo sec)
        {
            _pendingSection = null;
            webView.NavigateToString(BuildStreetViewHtml(sec.Lat, sec.Lon));
            lblCaptureStatus.Text = "Drag to look around, then click Capture.";
        }

        private static string BuildStreetViewHtml(double lat, double lon)
        {
            string latStr = lat.ToString(CultureInfo.InvariantCulture);
            string lonStr = lon.ToString(CultureInfo.InvariantCulture);
            string key    = GoogleApi.ApiKey;

            return $@"<!DOCTYPE html><html><head>
<style>
*{{margin:0;padding:0;box-sizing:border-box}}
html,body{{width:100%;height:100%;overflow:hidden}}
#sv{{width:100%;height:100%}}
</style></head><body>
<div id='sv'></div>
<script>
function getHeading(){{return window._pano?window._pano.getPov().heading:0;}}
function getPitch(){{return window._pano?window._pano.getPov().pitch:0;}}
function getFov(){{var z=window._pano?window._pano.getZoom():1;return Math.min(90/Math.pow(2,z-1),120);}}
function getPanoId(){{return window._pano?window._pano.getPano():'';}}
function initSV(){{
  window._pano=new google.maps.StreetViewPanorama(document.getElementById('sv'),{{
    position:{{lat:{latStr},lng:{lonStr}}},
    pov:{{heading:0,pitch:0}},zoom:1,
    addressControl:false,fullscreenControl:false,
    motionTracking:false,motionTrackingControl:false
  }});
}}
</script>
<script src='https://maps.googleapis.com/maps/api/js?key={key}&callback=initSV' async></script>
</body></html>";
        }

        // ── Capture ───────────────────────────────────────────────────────────

        private async void Capture_Click(object s, RoutedEventArgs e)
        {
            if (SelectedSection == null || !_webViewReady) return;
            btnCapture.IsEnabled  = false;
            lblCaptureStatus.Text = "Downloading…";
            try
            {
                string hStr    = await webView.CoreWebView2.ExecuteScriptAsync("getHeading()");
                string pStr    = await webView.CoreWebView2.ExecuteScriptAsync("getPitch()");
                string fStr    = await webView.CoreWebView2.ExecuteScriptAsync("getFov()");
                string panoRaw = await webView.CoreWebView2.ExecuteScriptAsync("getPanoId()");

                double heading = double.Parse(hStr, CultureInfo.InvariantCulture);
                double pitch   = double.Parse(pStr, CultureInfo.InvariantCulture);
                double fov     = double.Parse(fStr, CultureInfo.InvariantCulture);
                // JS returns a JSON string with surrounding quotes — strip them
                string panoId  = panoRaw.Trim('"');

                string savePath = Path.Combine(_folder, $"section_{SelectedSection.Id:D3}.jpg");
                bool ok = await new StreetViewService().DownloadImageAsync(
                    panoId, heading, pitch, fov, savePath);

                if (ok)
                {
                    SelectedSection.ImagePath = savePath;
                    imgCapture.Source         = TryLoadBitmap(savePath);
                    lblCaptureStatus.Text     =
                        $"Captured — heading {heading:F0}°  pitch {pitch:F1}°  fov {fov:F0}°";
                }
                else
                {
                    lblCaptureStatus.Text = "No Street View coverage at this location.";
                }
            }
            catch (Exception ex)
            {
                lblCaptureStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                btnCapture.IsEnabled = true;
            }
        }

        private void Thumbnail_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ShowPreview(SelectedSection?.ImagePath);
        }

        private void ShowPreview(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show("No image captured yet. Use 'Capture this view' first.",
                    "No Preview", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BitmapImage bi;
            try
            {
                bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource   = new Uri(path);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
            }
            catch { return; }

            var img  = new System.Windows.Controls.Image
                { Source = bi, Stretch = System.Windows.Media.Stretch.Uniform };
            var hint = new TextBlock
            {
                Text       = "Click anywhere or press Esc to close",
                Foreground = System.Windows.Media.Brushes.White,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(160, 0, 0, 0)),
                FontSize            = 11,
                Padding             = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin              = new Thickness(0, 0, 0, 10),
            };
            var grid = new Grid();
            grid.Children.Add(img);
            grid.Children.Add(hint);

            var win = new Window
            {
                Title                 = "Street View — captured image",
                Width                 = 1000, Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background            = System.Windows.Media.Brushes.Black,
                Content               = grid,
                ResizeMode            = ResizeMode.CanResize,
            };
            win.KeyDown             += (_, e2) => { if (e2.Key == System.Windows.Input.Key.Escape) win.Close(); };
            win.MouseLeftButtonDown += (_, _2) => win.Close();
            try { win.Owner = this; } catch { }
            win.ShowDialog();
        }

        // ── Section selection ─────────────────────────────────────────────────

        private void LstSections_Changed(object s, SelectionChangedEventArgs e)
        {
            if (lstSections.SelectedItem is not SectionInfo sec) return;
            SelectedSection = sec;

            // Restore or default the preset selection for this section
            string? saved = sec.SelectedPresetName ?? _presetNames.FirstOrDefault();
            cmbPreset.SelectedItem = saved != null && _presetNames.Contains(saved)
                ? saved
                : _presetNames.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(txtId.Text) || txtId.Text.StartsWith("Section "))
                txtId.Text = sec.ToString();

            imgCapture.Source     = TryLoadBitmap(sec.ImagePath);
            lblCaptureStatus.Text = sec.ImagePath != null
                ? "Previously captured — drag to re-capture if needed."
                : "Drag to look around, then click Capture.";

            if (_webViewReady) LoadPanorama(sec);
            else _pendingSection = sec;
        }

        private void PresetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbPreset.SelectedItem is not string name) return;
            if (SelectedSection != null)
                SelectedSection.SelectedPresetName = name;
        }

        private static BitmapImage? TryLoadBitmap(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource    = new Uri(path);
                bi.CacheOption  = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.EndInit();
                return bi;
            }
            catch { return null; }
        }

        // ── Confirm ───────────────────────────────────────────────────────────

        private void Append_Click(object s, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtId.Text))
            {
                MessageBox.Show("Enter a cross-section ID (e.g. Section 001).",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (cmbPreset.SelectedItem is not string preset)
            {
                MessageBox.Show("Select a preset layout before continuing.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string id = txtId.Text.Trim();
            // Prevent adding the same section ID twice
            if (Queue.Any(q => q.SectionId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"'{id}' is already queued.",
                    "Duplicate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Queue.Add(new QueueEntry(id, preset, SelectedSection?.ImagePath ?? "", SelectedSection?.StreetName ?? "",
                SelectedSection?.ModelX ?? 0, SelectedSection?.ModelY ?? 0));
            UpdateQueueStatus();

            // Advance to next un-queued section automatically
            var sections = lstSections.ItemsSource as System.Collections.IList;
            if (sections != null)
            {
                int next = lstSections.SelectedIndex + 1;
                if (next < sections.Count) lstSections.SelectedIndex = next;
            }
        }

        private void UpdateQueueStatus()
        {
            if (Queue.Count == 0)
                lblQueueStatus.Text = "No sections queued yet.";
            else
                lblQueueStatus.Text = string.Join("\n", Queue.Select(q => $"✓ {q.SectionId}  [{q.PresetName}]"));
            btnDone.IsEnabled = Queue.Count > 0;
        }

        private void AppendAll_Click(object s, RoutedEventArgs e)
        {
            if (cmbPreset.SelectedItem is not string preset)
            {
                MessageBox.Show("Select a preset layout before queuing all sections.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sections = (lstSections.ItemsSource as IList<SectionInfo>) ?? new List<SectionInfo>(0);
            int added = 0;
            foreach (var sec in sections)
            {
                string id = sec.ToString();
                if (Queue.Any(q => q.SectionId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                // Use the section's individually saved preset if it has one, else the current selection
                string sectionPreset = sec.SelectedPresetName ?? preset;
                Queue.Add(new QueueEntry(id, sectionPreset, sec.ImagePath ?? "", sec.StreetName ?? "",
                    sec.ModelX, sec.ModelY));
                added++;
            }
            if (added == 0)
                MessageBox.Show("All sections are already queued.", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else
                UpdateQueueStatus();
        }

        private void Done_Click(object s, RoutedEventArgs e) =>
            DialogResult = true;

        private void Cancel_Click(object s, RoutedEventArgs e) =>
            DialogResult = false;
    }
}
