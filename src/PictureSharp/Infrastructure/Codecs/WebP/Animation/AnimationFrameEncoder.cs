using System.Buffers;
using PictureSharp.Formats.WebP;

namespace PictureSharp.Infrastructure.Codecs.WebP.Animation;

/// <summary>
/// The frame loop of libwebp's WebPAnimEncoder, fed one canvas at a time. It keeps only the canvases and encoded
/// frames that a later keyframe decision can still change: with keyframe insertion that is the window since the
/// candidate range of the last keyframe (at most <c>KeyframeMax</c> frames), otherwise the previous frame.
/// </summary>
// port: src/mux/anim_encode.c WebPAnimEncoderAdd, CacheFrame, SetFrame, KeyFramePenalty
internal sealed class AnimationFrameEncoder(int width, int height, WebPEncoderOptions options, bool ownsCanvases) : IDisposable
{
    private readonly (int Minimum, int Maximum) _bounds = AnimationEncoder.KeyframeBounds(options);
    private readonly List<EncodedAnimationFrame> _frames = [];
    private readonly List<(Rgba32[] Pixels, int Duration)> _canvases = [];
    private int _firstFrame;
    private int _firstCanvas;
    private int _count;
    private int _lastKeyframe;
    private int _bestIndex = -1;
    private long _bestPenalty = long.MaxValue;
    private EncodedAnimationFrame? _bestKeyframe;
    private Image? _scratch;

    /// <summary>Number of canvases currently retained (for memory measurements).</summary>
    public int RetainedCanvases => _canvases.Count;

    private bool SearchesKeyframes => _bounds.Maximum is not (int.MaxValue or 1);

    /// <summary>
    /// Encodes the next canvas. With <c>ownsCanvases</c> the array comes from <see cref="ArrayPool{T}.Shared"/> and is
    /// returned when no longer needed; otherwise it must stay unchanged until the encoder is disposed.
    /// </summary>
    public void Add(Rgba32[] canvas, int duration, CancellationToken cancellationToken)
    {
        int i = _count++;
        _canvases.Add((canvas, duration));
        (int minimum, int maximum) = _bounds;
        bool forceKeyframe = i == 0 || maximum == 1;
        _frames.Add(SetFrame(i, forceKeyframe, cancellationToken));
        if (!forceKeyframe && maximum != int.MaxValue)
        {
            int distance = i - _lastKeyframe;
            if (distance > minimum)
            {
                EncodedAnimationFrame keyframe = SetFrame(i, true, cancellationToken);
                long penalty = keyframe.Image.DiskSize - Frame(i).Image.DiskSize;
                if (penalty <= _bestPenalty)
                {
                    _bestPenalty = penalty;
                    _bestIndex = i;
                    _bestKeyframe = keyframe;
                }
            }

            if (distance >= maximum)
            {
                _frames[_bestIndex - _firstFrame] = _bestKeyframe!;
                Frame(_bestIndex - 1).DisposeBackground = false;
                // A selected keyframe changes its disposal rectangle; rebuild the buffered suffix.
                for (int j = _bestIndex + 1; j <= i; j++)
                {
                    _frames[j - _firstFrame] = SetFrame(j, false, cancellationToken);
                }

                _lastKeyframe = _bestIndex;
                _bestPenalty = long.MaxValue;
                _bestIndex = -1;
                _bestKeyframe = null;
            }
        }

        // Later frames read the previous canvas; a keyframe selection rebuilds frames after its candidate range.
        long keepFrom = SearchesKeyframes ? Math.Min(i, (long)_lastKeyframe + minimum + 1) : i;
        while (_firstCanvas < keepFrom)
        {
            Release(_canvases[0].Pixels);
            _canvases.RemoveAt(0);
            _firstCanvas++;
        }
    }

    /// <summary>Moves the frames that no later frame can change to <paramref name="output"/>.</summary>
    public void TakeFinished(List<EncodedAnimationFrame> output)
    {
        // The last frame's disposal is set by the next one; a keyframe selection changes frames from its candidate range on.
        int last = _count - 1;
        long limit = SearchesKeyframes ? Math.Min(last, (long)_lastKeyframe + _bounds.Minimum) : last;
        int count = (int)Math.Max(0, limit - _firstFrame);
        output.AddRange(_frames.GetRange(0, count));
        _frames.RemoveRange(0, count);
        _firstFrame += count;
    }

    /// <summary>Moves all remaining frames to <paramref name="output"/>.</summary>
    public void Finish(List<EncodedAnimationFrame> output)
    {
        output.AddRange(_frames);
        _firstFrame += _frames.Count;
        _frames.Clear();
    }

    private EncodedAnimationFrame Frame(int index) => _frames[index - _firstFrame];

    private ReadOnlySpan<Rgba32> Canvas(int index) => _canvases[index - _firstCanvas].Pixels.AsSpan(0, width * height);

    // port: src/mux/anim_encode.c SetFrame, GenerateCandidates, PickBestCandidate
    private EncodedAnimationFrame SetFrame(int index, bool keyframe, CancellationToken cancellationToken)
    {
        ReadOnlySpan<Rgba32> current = Canvas(index);
        int duration = _canvases[index - _firstCanvas].Duration;
        Rgba32[]? disposed = null;
        EncodedAnimationFrame? best = null;
        try
        {
            if (index == 0)
            {
                disposed = ArrayPool<Rgba32>.Shared.Rent(width * height);
                disposed.AsSpan(0, width * height).Clear();
                AnimationEncoder.GenerateCandidates(disposed.AsSpan(0, width * height), current, width, height, false, false, false,
                    duration, options, ref best, ref _scratch, cancellationToken);
            }
            else
            {
                ReadOnlySpan<Rgba32> previous = Canvas(index - 1);
                AnimationEncoder.GenerateCandidates(previous, current, width, height, keyframe, false, !keyframe, duration, options, ref best,
                    ref _scratch, cancellationToken);
                if (!keyframe)
                {
                    disposed = ArrayPool<Rgba32>.Shared.Rent(width * height);
                    previous.CopyTo(disposed);
                    AnimationEncoder.DisposeFrameRectangle(disposed.AsSpan(0, width * height), width, Frame(index - 1).Rectangle);
                    AnimationEncoder.GenerateCandidates(disposed.AsSpan(0, width * height), current, width, height, false, true, true,
                        duration, options, ref best, ref _scratch, cancellationToken);
                }
            }

            if (index > 0 && !keyframe)
            {
                Frame(index - 1).DisposeBackground = best!.DisposePrevious;
            }

            return best!;
        }
        finally
        {
            if (disposed is not null)
            {
                ArrayPool<Rgba32>.Shared.Return(disposed);
            }
        }
    }

    private void Release(Rgba32[] pixels)
    {
        if (ownsCanvases)
        {
            ArrayPool<Rgba32>.Shared.Return(pixels);
        }
    }

    public void Dispose()
    {
        foreach ((Rgba32[] pixels, _) in _canvases)
        {
            Release(pixels);
        }

        _canvases.Clear();
        _scratch?.Dispose();
        _scratch = null;
    }
}
