using System.Threading.Channels;
using DeChimex.Domain.Entities.Diagnostics;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Diagnostics;

/// <summary>
/// Bounded in-memory hand-off between request threads and the diagnostics writer. Recording
/// never blocks or throws on the request path; under extreme bursts the oldest pending items
/// are dropped and COUNTED (visible in the portal) rather than slowing the application.
/// </summary>
public sealed class DiagnosticsQueue
{
    private readonly Channel<object> _channel;
    private long _dropped;
    private long _captureFailures;
    private long _enqueued;
    private long _written;
    private long _writeFailures;

    public DiagnosticsQueue(IOptions<DevPortalOptions> options)
    {
        var capacity = Math.Max(100, options.Value.QueueCapacity);
        _channel = Channel.CreateBounded<object>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public void Enqueue(TechnicalEvent e) { if (_channel.Writer.TryWrite(e)) Interlocked.Increment(ref _enqueued); }
    public void Enqueue(IntegrationCall c) { if (_channel.Writer.TryWrite(c)) Interlocked.Increment(ref _enqueued); }

    public ChannelReader<object> Reader => _channel.Reader;

    public long Dropped => Interlocked.Read(ref _dropped);
    public long Enqueued => Interlocked.Read(ref _enqueued);
    public long Written => Interlocked.Read(ref _written);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    /// <summary>Events that could not even be built/queued (a recorder bug) — counted, never silent.</summary>
    public long CaptureFailures => Interlocked.Read(ref _captureFailures);
    public void CountCaptureFailure() => Interlocked.Increment(ref _captureFailures);
    public int Pending => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    internal void MarkWritten(int count) => Interlocked.Add(ref _written, count);
    internal void MarkWriteFailure(int count) => Interlocked.Add(ref _writeFailures, count);
}
