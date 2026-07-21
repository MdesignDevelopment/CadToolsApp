using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CadToolsApp.Models;
using Microsoft.Win32;

namespace CadToolsApp.Dialogs
{
    public partial class CrossSectionDialog : Window
    {
        private readonly ObservableCollection<LayoutZone> _zones = new();
        private bool _sync;
        private bool _initialized;

        public string           SectionId         { get; private set; } = "";
        public string           TemplatePath      { get; private set; } = "";
        public SectionInfo?     SelectedSection   { get; private set; }
        public string           SelectedImagePath { get; private set; } = "";
        public double           InsertX           { get; private set; }
        public double           InsertY           { get; private set; }
        public List<LayoutZone> Zones             => _zones.ToList();

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
            ZoneType.Voetpad, ZoneType.RijbaanFront, ZoneType.RijbaanBack,
        };

        private static readonly ZoneType[] SideTypes =
        {
            ZoneType.Berm, ZoneType.BermTrees, ZoneType.Fietspad, ZoneType.Voetpad,
        };

        public CrossSectionDialog(IList<SectionInfo> sections, string templatePath)
        {
            InitializeComponent();
            _initialized = true;
            UpdateImageBorders();

            lstZones.ItemsSource    = _zones;
            lstSections.ItemsSource = sections;
            TemplatePath            = templatePath ?? "";
            txtTemplate.Text        = TemplatePath;

            RebuildButtons(FullTypes);
            RebuildTypeCombo(FullTypes);
            ApplyPresets(FullPresets);

            if (sections.Count > 0) lstSections.SelectedIndex = 0;
        }

        private void LstSections_Changed(object s, SelectionChangedEventArgs e)
        {
            if (lstSections.SelectedItem is not SectionInfo sec) return;
            SelectedSection = sec;
            if (string.IsNullOrWhiteSpace(txtId.Text) || txtId.Text.StartsWith("Section "))
                txtId.Text = sec.ToString();

            imgLeft.Source  = TryLoadBitmap(sec.LeftImagePath);
            imgRight.Source = TryLoadBitmap(sec.RightImagePath);
            UpdateSelectedImagePath();
        }

        private static BitmapImage? TryLoadBitmap(string path)
        {
            if (!File.Exists(path)) return null;
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

        private void ImgLeft_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_initialized) return;
            rbImgLeft.IsChecked = true;
            if (e.ClickCount == 2) ShowPreview(SelectedSection?.LeftImagePath, "Left view — Street View");
        }

        private void ImgRight_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_initialized) return;
            rbImgRight.IsChecked = true;
            if (e.ClickCount == 2) ShowPreview(SelectedSection?.RightImagePath, "Right view — Street View");
        }

        private void PreviewLeft_Click(object sender, RoutedEventArgs e)
            => ShowPreview(SelectedSection?.LeftImagePath, "Left view — Street View");

        private void PreviewRight_Click(object sender, RoutedEventArgs e)
            => ShowPreview(SelectedSection?.RightImagePath, "Right view — Street View");

        private void ShowPreview(string? path, string title)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show(
                    "No image available.\nRun Street View first to download images.",
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
                Title = title, Width = 1000, Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = System.Windows.Media.Brushes.Black,
                Content    = grid, ResizeMode = ResizeMode.CanResize,
            };
            win.KeyDown            += (_, e2) => { if (e2.Key == System.Windows.Input.Key.Escape) win.Close(); };
            win.MouseLeftButtonDown += (_, _2) => win.Close();
            try { win.Owner = this; } catch { }
            win.ShowDialog();
        }

        private void ImgSelection_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            UpdateImageBorders();
            UpdateSelectedImagePath();
        }

        private void UpdateImageBorders()
        {
            if (borderLeft == null || borderRight == null || rbImgLeft == null) return;
            bool left = rbImgLeft.IsChecked == true;
            var sel   = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x1E, 0x90, 0xFF));
            var unsel = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA));
            borderLeft.BorderBrush  = left  ? sel : unsel;
            borderRight.BorderBrush = !left ? sel : unsel;
        }

        private void UpdateSelectedImagePath()
        {
            if (SelectedSection == null) return;
            bool left = rbImgLeft?.IsChecked == true;
            SelectedImagePath = left ? SelectedSection.LeftImagePath
                                     : SelectedSection.RightImagePath;
        }

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

        private void Browse_Click(object s, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title  = "Select template DXF/DWG with XSEC_* blocks",
                Filter = "DXF Drawing (*.dxf)|*.dxf",
            };
            if (ofd.ShowDialog() == true)
            {
                TemplatePath     = ofd.FileName;
                txtTemplate.Text = ofd.FileName;
            }
        }

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
