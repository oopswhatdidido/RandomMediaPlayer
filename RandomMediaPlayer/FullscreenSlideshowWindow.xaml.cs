using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vlc.DotNet.Wpf;

namespace RandomMediaPlayer
{
    public partial class FullscreenSlideshowWindow : Window
    {
        private bool _vlcReady;
        private MainWindow? _ownerMain;
        // Tracked separately from the hyperlink text so the click handler
        // gets a canonical path even if the visible string is truncated.
        private string? _currentOverlayPath;

        // Ping-pong VLC instances. _activeVlc holds the currently visible
        // playback; _preloadVlc holds the next clip, opened ahead of time and
        // paused at frame 0 (via :start-paused). At advance, we swap.
        private VlcControl _activeVlc = null!;
        private VlcControl _preloadVlc = null!;
        // Path currently parked on _preloadVlc. When PlayVideo/PlayGif arrives
        // with this path, we take the fast resume-from-paused branch; otherwise
        // (preload missed or stale) we fall through to a cold open.
        private string? _preloadedPath;

        // Path / kind currently playing on _activeVlc. Used by ReplayCurrent
        // so the main window can drive every monitor's hold-mode loop from
        // its EndReached event - each monitor's own VLC EndReached is not
        // subscribed to, so without this fan-out the monitors freeze on the
        // last frame while only the main preview pane loops.
        private string? _activePath;
        private MediaKind _activeKind;

        // libvlc's built-in gif demuxer only emits the first frame; the
        // avformat (FFmpeg) demuxer iterates frames correctly. :start-paused
        // makes the player decode the first frame then halt so the swap at
        // advance is just a Play() (resume).
        private static readonly string[] GifPreloadOptions = { ":demux=avformat", ":start-paused" };
        private static readonly string[] VideoPreloadOptions = { ":start-paused" };
        private static readonly string[] GifColdOptions = { ":demux=avformat" };
        private static readonly string[] VideoColdOptions = Array.Empty<string>();

        public FullscreenSlideshowWindow(MainWindow? ownerMain = null)
        {
            InitializeComponent();
            _ownerMain = ownerMain;
            _activeVlc = VlcCanvas;
            _preloadVlc = VlcCanvasB;
            InitializeVlc();
        }

        private void InitializeVlc()
        {
            // Reuse the same lookup MainWindow uses (system VLC install,
            // app-relative libvlc folder, etc.) so both windows agree.
            var dir = MainWindow.FindVlcLibDirectory();
            if (dir == null)
            {
                Debug.WriteLine("[VLC] Fullscreen: libvlc not found.");
                return;
            }

            try
            {
                foreach (var canvas in new[] { VlcCanvas, VlcCanvasB })
                {
                    canvas.SourceProvider.CreatePlayer(dir, Array.Empty<string>());
                    var mp = canvas.SourceProvider.MediaPlayer;
                    if (mp == null) continue;
                    mp.Audio.Volume = 0;
                    // Each canvas watches its own EndReached so this monitor
                    // can self-loop in hold mode. We capture the canvas in the
                    // closure so the handler knows which one fired - libvlc's
                    // sender argument is the MediaPlayer, not the WPF control.
                    var local = canvas;
                    mp.EndReached += (s, e) => Dispatcher.BeginInvoke(() => OnVlcEndReached(local));
                }
                _vlcReady = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VLC] Fullscreen init failed: {ex.Message}");
            }
        }

        public void DisplayImage(BitmapImage image, bool scaleToFill)
        {
            // Coming from VLC -> stop the source we're leaving.
            PauseActiveVlcSafely();
            FullscreenImage.Stretch = scaleToFill ? Stretch.UniformToFill : Stretch.Uniform;
            FullscreenImage.Source = image;
            ShowImage();
            _activePath = null;
        }

        // Called from MainWindow during PreloadNextAsync / PreloadForContextAsync.
        // Opens the next media on the hidden VLC instance so it's ready to
        // reveal instantly when the advance fires.
        public void PreloadMedia(string path, MediaKind kind)
        {
            if (!_vlcReady) return;
            // Don't preload onto the canvas that's currently displaying.
            var mp = _preloadVlc.SourceProvider.MediaPlayer;
            if (mp == null) return;

            try
            {
                var options = kind == MediaKind.Gif ? GifPreloadOptions : VideoPreloadOptions;
                mp.Play(new Uri(path), options);
                _preloadedPath = path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VLC] Preload failed: {ex.Message}");
                _preloadedPath = null;
            }
        }

        public void PlayGif(string path, bool scaleToFill) =>
            PlayInVlc(path, 0, MediaKind.Gif);

        public void PlayVideo(string path, int startTimeMs) =>
            PlayInVlc(path, startTimeMs, MediaKind.Video);

        private void PlayInVlc(string path, int startTimeMs, MediaKind kind)
        {
            if (!_vlcReady) return;
            var nextPlayer = _preloadVlc.SourceProvider.MediaPlayer;
            if (nextPlayer == null) return;

            if (string.Equals(_preloadedPath, path, StringComparison.Ordinal))
            {
                // Preload hit: media is already opened and paused at frame 0
                // on _preloadVlc. Seek (videos only) then resume.
                try
                {
                    if (kind == MediaKind.Video && startTimeMs > 0)
                        nextPlayer.Time = startTimeMs;
                    nextPlayer.Play();
                }
                catch (Exception ex) { Debug.WriteLine($"[VLC] Resume failed: {ex.Message}"); }
            }
            else
            {
                // Cold path: preload didn't run (first advance, very short
                // delay between advances, or the upstream preload picked a
                // different file). Open + play on the hidden canvas, then
                // reveal it below. The previously-preloaded media on this
                // player gets released by libvlc when we Play a new URI.
                try
                {
                    var options = kind == MediaKind.Gif ? GifColdOptions : VideoColdOptions;
                    nextPlayer.Play(new Uri(path), options);
                    if (kind == MediaKind.Video && startTimeMs > 0)
                        nextPlayer.Time = startTimeMs;
                }
                catch (Exception ex) { Debug.WriteLine($"[VLC] Cold play failed: {ex.Message}"); }
            }

            // Reveal the new active canvas before pausing the old one - swap
            // ordering matters: hiding+showing in the same layout pass avoids
            // a one-frame black flash.
            ShowVlc(_preloadVlc);
            try { _activeVlc.SourceProvider.MediaPlayer?.Pause(); } catch { }

            // Swap roles: the canvas we just revealed is now active; the
            // previously-active canvas becomes the preload target for next time.
            (_activeVlc, _preloadVlc) = (_preloadVlc, _activeVlc);
            _preloadedPath = null;
            _activePath = path;
            _activeKind = kind;
        }

        // Self-loop handler: when this monitor's currently-visible VLC reaches
        // end during hold mode, restart it. We ignore EndReached on the
        // preload canvas and we ignore it when not held - in the latter case
        // the main window's EndReached drives advancement for everyone.
        private void OnVlcEndReached(VlcControl source)
        {
            if (source != _activeVlc) return;
            if (_ownerMain?.IsOnHold != true) return;
            ReplayCurrent();
        }

        // Re-plays the current clip on _activeVlc from the start. Used by
        // OnVlcEndReached for the hold-mode loop. Deliberately doesn't touch
        // _preloadVlc - the preloaded next clip stays ready for when hold is
        // released.
        public void ReplayCurrent()
        {
            if (!_vlcReady || _activePath == null) return;
            var mp = _activeVlc.SourceProvider.MediaPlayer;
            if (mp == null) return;
            try
            {
                var options = _activeKind == MediaKind.Gif ? GifColdOptions : VideoColdOptions;
                mp.Play(new Uri(_activePath), options);
            }
            catch (Exception ex) { Debug.WriteLine($"[Hold] VLC replay failed: {ex.Message}"); }
        }

        private void PauseActiveVlcSafely()
        {
            try
            {
                var mp = _activeVlc.SourceProvider.MediaPlayer;
                if (mp != null) mp.Pause();
            }
            catch (Exception ex) { Debug.WriteLine($"VLC pause failed: {ex.Message}"); }
        }

        // Mirror of MainWindow.StopPlaybackOfOtherKinds. Video and Gif both
        // play in VLC, so it only needs pausing when switching to a still image.
        public void StopPlaybackOfOtherKinds(MediaKind incoming)
        {
            if (incoming == MediaKind.Image) PauseActiveVlcSafely();
        }

        private void ShowImage()
        {
            FullscreenImage.Visibility = Visibility.Visible;
            VlcCanvas.Visibility = Visibility.Collapsed;
            VlcCanvasB.Visibility = Visibility.Collapsed;
        }

        private void ShowVlc(VlcControl canvas)
        {
            FullscreenImage.Visibility = Visibility.Collapsed;
            VlcCanvas.Visibility = canvas == VlcCanvas ? Visibility.Visible : Visibility.Collapsed;
            VlcCanvasB.Visibility = canvas == VlcCanvasB ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Esc closes the fullscreen view but leaves the slideshow running.
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }

            // Forward Space / Del / Left / Right to the main window so the
            // user gets identical key behavior in both views.
            _ownerMain?.HandleHotKey(e);
        }

        // Updates the bottom-edge clickable file-path overlay on this window.
        // Pass show=false (or path=null) to hide the overlay entirely.
        public void SetPathOverlay(string? path, bool show)
        {
            if (!show || string.IsNullOrEmpty(path))
            {
                PathOverlayBorder.Visibility = Visibility.Collapsed;
                _currentOverlayPath = null;
                return;
            }
            _currentOverlayPath = path;
            PathOverlayHyperlink.Inlines.Clear();
            PathOverlayHyperlink.Inlines.Add(new Run(path));
            PathOverlayBorder.Visibility = Visibility.Visible;
        }

        private void PathOverlayHyperlink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentOverlayPath)) return;
            if (!File.Exists(_currentOverlayPath)) return;
            try
            {
                Process.Start("explorer.exe", $"/select,\"{_currentOverlayPath}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] Open file location failed: {ex.Message}");
            }
        }
    }
}
