using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ThinCam;

internal sealed class FrameQueue
{
    private readonly object _gate = new();
    private readonly Channel<VideoFrame> _channel;
    private bool _completed;

    internal FrameQueue(int capacity)
    {
        // DropOldest lets the channel itself evict the oldest frame when full. The callback
        // disposes the evicted frame, so the writer never reads from the channel (which would
        // violate the SingleReader contract).
        _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        }, static dropped => dropped.Dispose());
    }

    internal void Publish(VideoFrame frame)
    {
        lock (_gate)
        {
            if (_completed)
            {
                frame.Dispose();
                return;
            }

            if (!_channel.Writer.TryWrite(frame))
                frame.Dispose();
        }
    }

    internal void Complete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            _channel.Writer.TryComplete(error);
        }
    }

    internal void Drain()
    {
        lock (_gate)
        {
            while (_channel.Reader.TryRead(out var frame))
                frame.Dispose();
        }
    }

    internal async IAsyncEnumerable<VideoFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }
}
