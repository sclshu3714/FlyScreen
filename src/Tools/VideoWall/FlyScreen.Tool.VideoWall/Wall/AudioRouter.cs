using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Wall;

/// <summary>整墙音频：静音 / 独奏（混音属 P2，不支持）。</summary>
public sealed class AudioRouter
{
    private AudioPolicy _policy = AudioPolicy.Mute;
    private int? _soloTile;

    public AudioPolicy Policy
    {
        get => _policy;
        set => _policy = value;
    }

    public int? SoloTile
    {
        get => _soloTile;
        set => _soloTile = value;
    }

  public void Apply(IReadOnlyList<TileSession> tiles)
    {
        if (_policy == AudioPolicy.Mix)
        {
            foreach (var tile in tiles)
                tile.SetMuted(true);
            return;
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var muted = _policy switch
            {
                AudioPolicy.Mute => true,
                AudioPolicy.Solo => _soloTile != tile.Index,
                _ => true
            };
            tile.SetMuted(muted);
        }
    }

    public void MuteAll(IReadOnlyList<TileSession> tiles)
    {
        _policy = AudioPolicy.Mute;
        _soloTile = null;
        Apply(tiles);
    }

    public void SoloTileAt(IReadOnlyList<TileSession> tiles, int index)
    {
        _policy = AudioPolicy.Solo;
        _soloTile = index;
        Apply(tiles);
    }
}