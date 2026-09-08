using System.IO;
using System.Windows.Media;

namespace RuneFoundry.UI;

/// <summary>
/// Plays one sound at a time, and can pause it.
///
/// This replaces SoundPlayer, which — the part that actually matters for a button that
/// changes shape — never said when a clip had finished. A play button that turns into a
/// stop button has to turn back by itself when the sound ends, and MediaPlayer raises
/// <see cref="MediaPlayer.MediaEnded"/> where SoundPlayer raises nothing.
///
/// The cost is that MediaPlayer holds the file open while it has one loaded, and these are
/// files the user is in the middle of replacing. So stopping closes it, and moving to a
/// different sound closes the last one first.
/// </summary>
public sealed class AudioPlayer
{
    private readonly MediaPlayer _player = new();
    private string? _path;
    private bool _playing;

    /// <summary>Raised whenever the button should be redrawn: started, paused, or finished.</summary>
    public event Action? Changed;

    public AudioPlayer()
    {
        _player.MediaEnded += (_, _) =>
        {
            // Back to the beginning, so the same button starts it again rather than
            // resuming at the end and appearing to do nothing.
            _player.Position = TimeSpan.Zero;
            _player.Pause();
            _playing = false;
            Changed?.Invoke();
        };

        _player.MediaFailed += (_, _) =>
        {
            _playing = false;
            Changed?.Invoke();
        };
    }

    /// <summary>The file currently loaded, playing or paused.</summary>
    public string? Current => _path;

    public bool IsPlaying => _playing;

    /// <summary>Whether this particular file is the one playing right now.</summary>
    public bool IsPlaying_(string path) =>
        _playing && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this file is loaded at all — playing or paused part-way through.</summary>
    public bool IsCurrent(string path) =>
        _path is not null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts a sound, or stops it if it is the one already playing. Returns an error to
    /// show, or null if it went fine.
    /// </summary>
    public string? Toggle(string path)
    {
        try
        {
            // Pressing it again stops rather than pauses. These are one- and two-second
            // voice lines; resuming half a sentence is not something anyone wants, and it
            // means the file is let go of immediately instead of while paused.
            if (IsCurrent(path) && _playing)
            {
                Close();
                Changed?.Invoke();
                return null;
            }

            if (!File.Exists(path)) return $"There is no {Path.GetFileName(path)} to play.";

            Close();

            _player.Open(new Uri(path));
            _player.Play();
            _path = path;
            _playing = true;
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            _playing = false;
            Changed?.Invoke();
            return ex.Message;
        }
    }

    /// <summary>Stops and lets go of the file, so it can be replaced or deleted.</summary>
    public void Stop()
    {
        if (_path is null && !_playing) return;

        Close();
        Changed?.Invoke();
    }

    private void Close()
    {
        try
        {
            _player.Stop();
            _player.Close();
        }
        catch
        {
            // Closing a player that never opened anything is not worth reporting.
        }

        _path = null;
        _playing = false;
    }
}
