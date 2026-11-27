using System.IO;
using System.Media;

namespace ESCaptureSketch;

/// <summary>캡처 때 내는 짧은 "찰칵" 소리. exe 에 넣은 음원을 처음 쓸 때 한 번 읽어 둔다.</summary>
public static class ShutterSound
{
    private static SoundPlayer? _player;
    private static int _volume;

    /// <param name="volume">소리 크기 0~100 (%). 0 이면 내지 않는다.</param>
    public static void Play(int volume)
    {
        if (volume <= 0) return;
        try { Player(volume).Play(); }   // 다른 스레드에서 재생하므로 캡처 흐름을 붙잡지 않는다
        catch (Exception ex) { Log.Write("캡처 소리 재생 실패: " + ex.Message); }
    }

    /// <summary>개발용: 소리를 내지 않고 만든 음원이 읽히는지만 확인한다.</summary>
    internal static string Check()
    {
        var p = Player(100);
        p.Load();
        return $"음원 {p.Stream!.Length}바이트, 읽기 {p.IsLoadCompleted}";
    }

    private static SoundPlayer Player(int volume)
    {
        volume = Math.Clamp(volume, 1, 100);
        if (_player != null && _volume == volume) return _player;
        _player?.Stop();   // 재생 중인 음원의 메모리를 버리기 전에 멈춘다
        // 음원: camera_snap1.wav by thecheeseman (freesound.org/s/51360, CC BY 4.0) — THIRD-PARTY-NOTICES.md. exe 안에 넣어 둔다.
        using var resource = typeof(ShutterSound).Assembly.GetManifestResourceStream("shutter.wav")
            ?? throw new InvalidOperationException("캡처 소리 음원이 없습니다.");
        var copy = new MemoryStream();
        resource.CopyTo(copy);
        var wav = copy.ToArray();
        Attenuate(wav, volume);
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load();
        _volume = volume;
        return _player = player;
    }

    /// <summary>
    /// SoundPlayer 에는 음량 조절이 없어, 16비트 PCM 음원의 표본값을 줄여 둔다.
    /// 귀에 느끼는 크기에 가깝게 제곱 곡선을 쓴다 (50% = 약 -12dB). 100% 는 원본 그대로.
    /// </summary>
    internal static void Attenuate(byte[] wav, int volume)
    {
        if (volume >= 100) return;
        var gain = volume / 100.0 * (volume / 100.0);
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var size = BitConverter.ToInt32(wav, at + 4);
            if (size < 0) return;
            if (wav[at] == 'd' && wav[at + 1] == 'a' && wav[at + 2] == 't' && wav[at + 3] == 'a')
            {
                var end = (int)Math.Min(wav.Length, at + 8L + size);
                for (var i = at + 8; i + 1 < end; i += 2)
                {
                    var v = (short)Math.Round(BitConverter.ToInt16(wav, i) * gain);
                    wav[i] = (byte)v;
                    wav[i + 1] = (byte)(v >> 8);
                }
                return;
            }
            at += 8 + size + (size & 1);
        }
    }
}
