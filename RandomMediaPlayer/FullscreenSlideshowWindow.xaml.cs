using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RandomMediaPlayer
{
    public partial class FullscreenSlideshowWindow : Window
    {
        private bool _vlcReady;
        private MainWindow? _ownerMain;

        public FullscreenSlideshowWindow(MainWindow? ownerMain = null)
        {
            InitializeComponent();
            _ownerMain = ownerMain;
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
                VlcCanvas.SourceProvider.CreatePlayer(dir, Array.Empty<string>());
                if (VlcCanvas.SourceProvider.MediaPlayer != null)
                    VlcCanvas.SourceProvider.MediaPlayer.Audio.Volume = 0;
                _vlcReady = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VLC] Fullscreen init failed: {ex.Message}");
            }
        }

        public void DisplayImage(BitmapImage image, bool scaleToFill)
        {
            // Coming from VLC or GIF -> stop the audio/animation source we're
            // leaving, but never the one we're about to use.
            PauseVlcSafely();
            StopGifSafely();
            FullscreenImage.Stretch = scaleToFill ? Stretch.UniformToFill : Stretch.Uniform;
            FullscreenImage.Source = image;
            ShowOnly(FullscreenImage);
        }

        public void PlayGif(string path, bool scaleToFill)
        {
            // Switching to GIF -> pause VLC. Don't touch the GIF element other
            // than to hand it the new source.
            PauseVlcSafely();
            GifPlayer.Stretch = scaleToFill ? Stretch.UniformToFill : Stretch.Uniform;
            GifPlayer.Source = new Uri(path, UriKind.Absolute);
            ShowOnly(GifPlayer);
            GifPlayer.Play();
        }

        public void PlayVideo(string path, int startTimeMs)
        {
            if (!_vlcReady) return;
            // Switching to VLC -> stop GIF. CRITICALLY do NOT call Stop on the
            // VLC player we're about to reuse: libvlc's Stop blocks waiting for
            // its internal media thread to wind down, and combined with a still-
            // firing EndReached callback that produces a UI-thread deadlock.
            // libvlc handles the media transition itself when Play is called
            // with a new URI.
            StopGifSafely();
            ShowOnly(VlcCanvas);
            var mp = VlcCanvas.SourceProvider.MediaPlayer;
            if (mp == null) return;
            mp.Play(new Uri(path));
            mp.Time = startTimeMs;
        }

        private void PauseVlcSafely()
        {
            try
            {
                if (_vlcReady && VlcCanvas.SourceProvider.MediaPlayer != null)
                    VlcCanvas.SourceProvider.MediaPlayer.Pause();
            }
            catch (Exception ex) { Debug.WriteLine($"VLC pause failed: {ex.Message}"); }
        }

        private void StopGifSafely()
        {
            try
            {
                GifPlayer.Stop();
                GifPlayer.Source = null;
            }
            catch (Exception ex) { Debug.WriteLine($"GifPlayer stop failed: {ex.Message}"); }
        }

        // Mirror of MainWindow.StopPlaybackOfOtherKinds - quiet the players we're
        // not about to use. Pause (not Stop) on VLC to avoid the wind-down deadlock.
        public void StopPlaybackOfOtherKinds(MediaKind incoming)
        {
            if (incoming != MediaKind.Video) PauseVlcSafely();
            if (incoming != MediaKind.Gif) StopGifSafely();
        }

        private void ShowOnly(UIElement element)
        {
            FullscreenImage.Visibility = element == FullscreenImage ? Visibility.Visible : Visibility.Collapsed;
            VlcCanvas.Visibility = element == VlcCanvas ? Visibility.Visible : Visibility.Collapsed;
            GifPlayer.Visibility = element == GifPlayer ? Visibility.Visible : Visibility.Collapsed;
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
    }
}
