using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CadToolsApp.Models;
using CadToolsApp.Services;
using Microsoft.Win32;

namespace CadToolsApp.Dialogs
{
    public partial class CrossSectionDialog : Window
    {
        private readonly ObservableCollection<LayoutZone> _zones = new();
        private bool _sync;
        private bool _initialized;
        private bool _webViewReady;
        private SectionInfo? _pendingSection;
        private readonly string _folder;

        public string           SectionId        { get; private set; } = "";
        public SectionInfo?     SelectedSection  { get; private set; }
        public string           SelectedImagePath => SelectedSection?.ImagePath ?? "";
        public double           InsertX          { get; private set; }
        public double           InsertY          { get; private set; }
        public List<LayoutZone> Zones            => _zones.ToList();

        private static readonly ZonePreset[] FullPresets =
        {
            new("Voetpad - Rijbaan - Rijbaan - Voetpad",
                (ZoneType.Voetpad, 1.5), (ZoneType.RijbaanFront, 3.0),
                (ZoneType.RijbaanBack, 3.0), (ZoneType.Voetpad, 1.5)),

            new("Fietspad - Rijbaan - Rijbaan - Fietspad",
                (ZoneType.Fietspad, 2.0), (ZoneType.RijbaanFront, 3.0),
                (ZoneType.RijbaanBack, 3.0), (ZoneType.Fietspad, 2.0)),

            new("Berm - Fietspad - Voetpad - Rijbaan - Rijbaan - Voetpad - Fietspad - Berm",
                (ZoneType.Berm, 1.0), (ZoneType.Fietspad, 2.0), (ZoneType.Voetpad, 1.5),
                (ZoneType.RijbaanFront, 2.5), (ZoneType.RijbaanBack, 2.5),
                (ZoneType.Voetpad, 1.5), (ZoneType.Fietspad, 2.0), (ZoneType.Berm, 1.0)),

            new("Berm Trees - Fietspad - Voetpad - Rijbaan - Rijbaan - Voetpad - Fietspad - Berm Trees",
                (ZoneType.BermTrees, 1.5), (ZoneType.Fietspad, 2.0), (ZoneType.Voetpad, 1.5),
                (ZoneType.RijbaanFront, 2.5), (ZoneType.RijbaanBack, 2.5),
                (ZoneType.Voetpad, 1.5), (ZoneType.Fietspad, 2.0), (ZoneType.BermTrees, 1.5)),

            new("Voetpad - Rijbaan - Rijbaan - Voetpad - Fietspad",
                (ZoneType.Voetpad, 1.5), (ZoneType.RijbaanFront, 2.5),
                (ZoneType.RijbaanBack, 2.5), (ZoneType.Voetpad, 1.5), (ZoneType.Fietspad, 2.0)),
        };

        private static readonly ZonePreset[] SidePresets =
        {
            new("Voetpad",                 (ZoneType.Voetpad, 1.5)),
            new("Fietspad",                (ZoneType.Fietspad, 2.0)),
            new("Voetpad - Fietspad",      (ZoneType.Voetpad, 1.5), (ZoneType.Fietspad, 2.0)),
            new("Berm - Voetpad - Fietspad",
                (ZoneType.Berm, 1.0), (ZoneType.Voetpad, 1.5), (ZoneType.Fietspad, 2.0)),
            new("Berm Trees - Voetpad - Fietspad",
                (ZoneType.BermTrees, 1.5), (ZoneType.Voetpad, 1.5), (ZoneType.Fietspad, 2.0)),
        };

        private static readonly ZoneType[] FullTypes =
        {
            ZoneType.Berm, ZoneType.BermTrees, ZoneType.Fietspad,
            ZoneType.Voetpad, ZoneType.RijbaanFront, ZoneType.RijbaanBack, ZoneType.Parking,
        };

        private static readonly ZoneType[] SideTypes =
        {
            ZoneType.Berm, ZoneType.BermTrees, ZoneType.Fietspad, ZoneType.Voetpad,
        };

        public CrossSectionDialog(IList<SectionInfo> sections, string folder)
        {
            InitializeComponent();
            _folder      = folder;
            _initialized = true;

            lstZones.ItemsSource    = _zones;
            lstSections.ItemsSource = sections;

            RebuildButtons(FullTypes);
            RebuildTypeCombo(FullTypes);
            ApplyPresets(FullPresets);

            if (sections.Count > 0) lstSections.SelectedIndex = 0;

            Loaded += async (s, e) => await InitWebViewAsync();
        }

        // ── WebView2 initialisation ───────────────────────────────────────────

        private async Task InitWebViewAsync()
        {
            try
            {
                await webView.EnsureCoreWebView2Async(null);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webViewReady = true;
                btnCapture.IsEnabled = true;
                if (_pendingSection != null) LoadPanorama(_pendingSection);
            }
            catch
            {
                lblCaptureStatus.Text =
                    "Street View viewer unavailable (WebView2 runtime not found).";
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
html,body{{width:100%;height:100%;overflow:hidden;position:relative}}
#sv{{width:100%;height:100%}}
#ov{{position:absolute;top:0;left:0;width:100%;height:100%;z-index:20;pointer-events:none}}
.mask{{position:absolute;background:rgba(0,0,0,0.55);pointer-events:none}}
#fr{{position:absolute;border:2px solid #fff;cursor:move;pointer-events:all;
     z-index:21;user-select:none}}
#rsz{{position:absolute;bottom:2px;right:2px;width:16px;height:16px;
      cursor:nwse-resize;background:#fff;opacity:0.85;border-radius:2px}}
#lbl{{position:absolute;bottom:4px;left:4px;color:#fff;font:11px sans-serif;
      background:rgba(0,0,0,0.5);padding:2px 5px;border-radius:2px;pointer-events:none}}
</style></head><body>
<div id='sv'></div>
<div id='ov'>
  <div id='mT' class='mask' style='top:0;left:0;width:100%'></div>
  <div id='mB' class='mask' style='left:0;width:100%;bottom:0'></div>
  <div id='mL' class='mask' style='left:0'></div>
  <div id='mR' class='mask' style='right:0'></div>
  <div id='fr'>
    <div id='rsz'></div>
    <div id='lbl'>199 x 83</div>
  </div>
</div>
<script>
var AW=199,AH=83,BASE_FOV=120;
var fw,fh,fx,fy;

function initFrame(){{
  fw=window.innerWidth*0.78;
  fw=Math.min(fw,window.innerHeight*AW/AH);
  fh=fw*AH/AW;
  fx=(window.innerWidth-fw)/2;
  fy=(window.innerHeight-fh)/2;
  upd();
}}

function upd(){{
  fw=Math.max(40,Math.min(window.innerWidth,Math.min(fw,window.innerHeight*AW/AH)));
  fh=fw*AH/AW;
  fx=Math.max(0,Math.min(window.innerWidth-fw,fx));
  fy=Math.max(0,Math.min(window.innerHeight-fh,fy));
  var fr=document.getElementById('fr');
  fr.style.left=fx+'px';fr.style.top=fy+'px';fr.style.width=fw+'px';fr.style.height=fh+'px';
  var mT=document.getElementById('mT'),mB=document.getElementById('mB');
  var mL=document.getElementById('mL'),mR=document.getElementById('mR');
  mT.style.height=fy+'px';
  mB.style.top=(fy+fh)+'px';mB.style.height=(window.innerHeight-fy-fh)+'px';
  mL.style.top=fy+'px';mL.style.height=fh+'px';mL.style.width=fx+'px';
  var rx=fx+fw;
  mR.style.top=fy+'px';mR.style.height=fh+'px';mR.style.left=rx+'px';
  mR.style.width=(window.innerWidth-rx)+'px';
}}

window.addEventListener('resize',function(){{fw=Math.min(fw,window.innerWidth);upd();}});

var drag=false,resz=false,sx,sy,ox,oy,ow;
document.getElementById('fr').addEventListener('mousedown',function(e){{
  if(e.target.id==='rsz'){{resz=true;sx=e.clientX;ow=fw;}}
  else{{drag=true;sx=e.clientX;sy=e.clientY;ox=fx;oy=fy;}}
  e.stopPropagation();e.preventDefault();
}});
document.addEventListener('mousemove',function(e){{
  if(drag){{fx=ox+(e.clientX-sx);fy=oy+(e.clientY-sy);upd();}}
  else if(resz){{fw=Math.max(60,ow+(e.clientX-sx));upd();}}
}});
document.addEventListener('mouseup',function(){{drag=false;resz=false;}});

function getHeading(){{return window._pano?window._pano.getPov().heading:0;}}
function getPitch(){{var cy=fy+fh/2;return(0.5-cy/window.innerHeight)*90;}}
function getFov(){{return BASE_FOV*(fw/window.innerWidth);}}

function initSV(){{
  window._pano=new google.maps.StreetViewPanorama(document.getElementById('sv'),{{
    position:{{lat:{latStr},lng:{lonStr}}},
    pov:{{heading:0,pitch:0}},zoom:1,
    addressControl:false,fullscreenControl:false,
    motionTracking:false,motionTrackingControl:false
  }});
  initFrame();
}}
</script>
<script src='https://maps.googleapis.com/maps/api/js?key={key}&callback=initSV' async></script>
</body></html>";
        }

        // ── Capture ───────────────────────────────────────────────────────────

        private async void Capture_Click(object s, RoutedEventArgs e)
        {
            if (SelectedSection == null || !_webViewReady) return;
            btnCapture.IsEnabled = false;
            lblCaptureStatus.Text = "Downloading…";

            try
            {
                string hStr = await webView.CoreWebView2.ExecuteScriptAsync("getHeading()");
                string pStr = await webView.CoreWebView2.ExecuteScriptAsync("getPitch()");
                string fStr = await webView.CoreWebView2.ExecuteScriptAsync("getFov()");

                double heading = double.Parse(hStr, CultureInfo.InvariantCulture);
                double pitch   = double.Parse(pStr, CultureInfo.InvariantCulture);
                double fov     = double.Parse(fStr, CultureInfo.InvariantCulture);

                string savePath = Path.Combine(_folder, $"section_{SelectedSection.Id:D3}.jpg");
                bool ok = await new StreetViewService().DownloadImageAsync(
                    SelectedSection.Lat, SelectedSection.Lon, heading, pitch, fov, savePath);

                if (ok)
                {
                    SelectedSection.ImagePath = savePath;
                    imgCapture.Source = TryLoadBitmap(savePath);
                    lblCaptureStatus.Text =
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
            if (e.ClickCount == 2)
                ShowPreview(SelectedSection?.ImagePath);
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
                Text = "Click anywhere or press Esc to close",
                Foreground = System.Windows.Media.Brushes.White,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(160, 0, 0, 0)),
                FontSize = 11, Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 10),
            };
            var grid = new Grid();
            grid.Children.Add(img);
            grid.Children.Add(hint);

            var win = new Window
            {
                Title  = "Street View — captured image",
                Width  = 1000, Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = System.Windows.Media.Brushes.Black,
                Content    = grid, ResizeMode = ResizeMode.CanResize,
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
            if (string.IsNullOrWhiteSpace(txtId.Text) || txtId.Text.StartsWith("Section "))
                txtId.Text = sec.ToString();

            imgCapture.Source = TryLoadBitmap(sec.ImagePath);
            lblCaptureStatus.Text = sec.ImagePath != null
                ? "Previously captured — drag to re-capture if needed."
                : "Drag to look around, then click Capture.";

            if (_webViewReady) LoadPanorama(sec);
            else _pendingSection = sec;
        }

        private static BitmapImage? TryLoadBitmap(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource   = new Uri(path);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                return bi;
            }
            catch { return null; }
        }

        // ── Zone type toggle ──────────────────────────────────────────────────

        private void TypeChanged(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            bool side = rbSide?.IsChecked == true;
            RebuildButtons(side ? SideTypes   : FullTypes);
            RebuildTypeCombo(side ? SideTypes : FullTypes);
            ApplyPresets(side ? SidePresets   : FullPresets);
        }

        private void RebuildButtons(ZoneType[] types)
        {
            if (pnlButtons == null) return;
            pnlButtons.Children.Clear();
            foreach (var t in types)
            {
                var btn = new Button
                {
                    Content = ZoneMeta.Label(t), Tag = t,
                    Margin  = new Thickness(0, 0, 5, 4),
                    Padding = new Thickness(8, 3, 8, 3),
                    MinWidth = 80,
                };
                btn.Click += AddZoneBtn_Click;
                pnlButtons.Children.Add(btn);
            }
        }

        private void RebuildTypeCombo(ZoneType[] types)
        {
            if (cmbType == null) return;
            cmbType.ItemsSource   = types.Select(t => ZoneMeta.Label(t)).ToList();
            cmbType.SelectedIndex = -1;
        }

        private void AddZoneBtn_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is not ZoneType t) return;
            var z = new LayoutZone { Type = t, Width = ZoneMeta.DefaultWidth(t) };
            _zones.Add(z);
            lstZones.SelectedIndex = _zones.Count - 1;
            lstZones.ScrollIntoView(z);
        }

        private void ApplyPresets(ZonePreset[] presets)
        {
            if (cmbPreset == null) return;
            cmbPreset.ItemsSource   = presets;
            cmbPreset.SelectedIndex = -1;
            _zones.Clear();
            RefreshList();
        }

        private void PresetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbPreset.SelectedItem is not ZonePreset p) return;
            _zones.Clear();
            foreach (var (t, w) in p.Zones)
                _zones.Add(new LayoutZone { Type = t, Width = w });
            RefreshList();
            if (_zones.Count > 0) lstZones.SelectedIndex = 0;
        }

        // ── Zone editor ───────────────────────────────────────────────────────

        private void LstZones_Changed(object s, SelectionChangedEventArgs e)
        {
            if (lstZones.SelectedItem is not LayoutZone z) return;
            _sync = true;
            bool side  = rbSide?.IsChecked == true;
            var  types = side ? SideTypes : FullTypes;
            cmbType.SelectedIndex = Array.IndexOf(types, z.Type);
            txtWidth.Text = z.Width.ToString("0.##");
            _sync = false;
        }

        private void TypeEditChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_sync) return;
            if (lstZones.SelectedItem is not LayoutZone z) return;
            bool side  = rbSide?.IsChecked == true;
            var  types = side ? SideTypes : FullTypes;
            int  idx   = cmbType.SelectedIndex;
            if (idx >= 0 && idx < types.Length) { z.Type = types[idx]; RefreshList(); }
        }

        private void WidthChanged(object sender, TextChangedEventArgs e)
        {
            if (_sync) return;
            if (lstZones.SelectedItem is not LayoutZone z) return;
            if (double.TryParse(txtWidth.Text.Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double w) && w > 0)
            { z.Width = w; RefreshList(); }
        }

        private void RefreshList()
        {
            if (lstZones == null) return;
            int i = lstZones.SelectedIndex;
            lstZones.ItemsSource = null;
            lstZones.ItemsSource = _zones;
            lstZones.SelectedIndex = i;
        }

        private void Remove_Click(object s, RoutedEventArgs e)
        {
            int i = lstZones.SelectedIndex;
            if (i < 0 || _zones.Count == 0) return;
            _zones.RemoveAt(i);
            lstZones.SelectedIndex = Math.Min(i, _zones.Count - 1);
        }

        private void Up_Click(object s, RoutedEventArgs e)
        {
            int i = lstZones.SelectedIndex;
            if (i <= 0) return;
            _zones.Move(i, i - 1);
            lstZones.SelectedIndex = i - 1;
        }

        private void Down_Click(object s, RoutedEventArgs e)
        {
            int i = lstZones.SelectedIndex;
            if (i < 0 || i >= _zones.Count - 1) return;
            _zones.Move(i, i + 1);
            lstZones.SelectedIndex = i + 1;
        }

        // ── Template / insertion point ────────────────────────────────────────


        private void Draw_Click(object s, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtId.Text))
            {
                MessageBox.Show("Enter a cross-section ID (e.g. DD, CC').",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_zones.Count == 0)
            {
                MessageBox.Show("Select a preset or add at least one zone.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double x = 0, y = 0;
            double.TryParse(txtInsX.Text.Replace(',', '.'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out x);
            double.TryParse(txtInsY.Text.Replace(',', '.'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out y);

            SectionId = txtId.Text.Trim();
            InsertX   = x;
            InsertY   = y;
            DialogResult = true;
        }

        private void Cancel_Click(object s, RoutedEventArgs e) =>
            DialogResult = false;
    }
}
