using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using WpfScreenHelper;

namespace RandomMediaPlayer
{
    /// <summary>
    /// Per-monitor configuration + runtime playback state. Persisted bits
    /// (Selected, OrientationFilter) round-trip through AppSettings; runtime
    /// bits (Window, NextPath, NextImage, etc.) are reset on each app launch.
    /// </summary>
    public class MonitorContext : INotifyPropertyChanged
    {
        public int Index { get; }
        public Screen Screen { get; }
        public string DisplayLabel { get; }

        // Bound to the per-row orientation combo. Driven by user.
        public List<string> OrientationOptions { get; } = new() { "All", "Landscape", "Vertical" };

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; OnPropertyChanged(); } }
        }

        private string _orientationFilter = "All";
        public string OrientationFilter
        {
            get => _orientationFilter;
            set
            {
                if (_orientationFilter != value)
                {
                    _orientationFilter = value;
                    OnPropertyChanged();
                }
            }
        }

        // ----- Runtime state (not persisted) -----
        public FullscreenSlideshowWindow? Window { get; set; }
        public string? CurrentPath { get; set; }
        public MediaKind CurrentKind { get; set; } = MediaKind.Image;
        public string? NextPath { get; set; }
        public BitmapImage? NextImage { get; set; }

        // Per-monitor playback history for left-arrow back navigation in
        // independent mode. Capped externally by MainWindow.MaxHistory.
        public List<string> History { get; } = new();

        // Used to gate per-context advances (independent mode) so a tick can't
        // re-enter while a previous one is still in flight on the same context.
        public bool IsAdvancing { get; set; }
        public bool IsPreloading { get; set; }

        public MonitorContext(int index, Screen screen)
        {
            Index = index;
            Screen = screen;
            int w = (int)screen.Bounds.Width;
            int h = (int)screen.Bounds.Height;
            string suffix = screen.Primary ? "  [Primary]" : "";
            DisplayLabel = $"{screen.DeviceName}   {w} x {h}{suffix}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
