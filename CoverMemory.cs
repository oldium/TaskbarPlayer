using System.Security.Cryptography;

namespace TaskbarPlayer;

internal enum CoverStep
{
    /// <summary>What is shown stays.</summary>
    Keep,

    /// <summary>The song has no picture: show none.</summary>
    Clear,

    /// <summary>Read the song's picture and hand it to <see cref="CoverMemory.Take"/>.</summary>
    Load,
}

/// <summary>
/// Decides when a song's picture is read and whether it replaces the one shown. An app may announce a new title
/// while the picture of the song before is still attached, and the new picture a moment later: so a picture is
/// read again whenever the app announces the song again, and one equal to what is shown changes nothing.
/// </summary>
internal sealed class CoverMemory
{
    private string? _song;
    private bool _read;
    private byte[]? _shown; // digest of the picture shown; none while no picture is shown
    private string? _shownSong; // the song it was read for

    /// <summary>A refresh found this song, with or without a picture.</summary>
    /// <param name="announced">The app announced its media properties since the last refresh.</param>
    public CoverStep See(string song, bool hasPicture, bool announced)
    {
        bool another = song != _song;
        if (another)
        {
            _song = song;
            _read = false;
        }
        if (!hasPicture)
        {
            // Artwork often comes a moment after the title: taken when it is there.
            if (!another) return CoverStep.Keep;
            _shown = null;
            _shownSong = null;
            return CoverStep.Clear;
        }
        if (_read && !announced) return CoverStep.Keep;
        _read = true;
        return CoverStep.Load;
    }

    /// <summary>
    /// The picture read for <paramref name="song"/>, none when it could not be read. True when what is shown has
    /// to change to it; false when the song is over by now or this picture is the one shown.
    /// </summary>
    public bool Take(string song, byte[]? picture)
    {
        if (song != _song) return false;

        if (picture is null)
        {
            // Unreadable this time. A cover read for this very song stays; one left from the song before goes.
            if (_shown is null || _shownSong == song) return false;
            _shown = null;
            _shownSong = null;
            return true;
        }

        byte[] digest = SHA256.HashData(picture);
        if (_shown is not null && digest.AsSpan().SequenceEqual(_shown)) return false;
        _shown = digest;
        _shownSong = song;
        return true;
    }

    /// <summary>No session any more: nothing is shown.</summary>
    public void Reset()
    {
        _song = null;
        _read = false;
        _shown = null;
        _shownSong = null;
    }
}
