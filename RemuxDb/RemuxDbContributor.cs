using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.RemuxDb;

/// <summary>
/// Sends probes of files RemuxDB does not know yet, one at a time in the background, so playback
/// never waits on it. Only files whose torrent is known are sent: the torrent is what lets other
/// clients match the entry. Nothing identifying the server or its users goes along, only the
/// file's name, size, tracks and the title's ids.
/// </summary>
public sealed class RemuxDbContributor(
    RemuxDbClient client,
    IMemoryCache cache,
    ILogger<RemuxDbContributor> log
) : BackgroundService
{
    private const int MaxPending = 20;
    private static readonly TimeSpan SentTtl = TimeSpan.FromDays(1);

    private readonly Channel<(RemuxDbTitle Title, RemuxDbSubmission Submission)> _queue =
        Channel.CreateBounded<(RemuxDbTitle, RemuxDbSubmission)>(
            new BoundedChannelOptions(MaxPending)
            {
                // Wait, not DropWrite: with it TryWrite reports a dropped item as written.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            }
        );

    /// <summary>Queues a submission unless the same file was queued or sent recently.</summary>
    public void Enqueue(RemuxDbTitle title, RemuxDbSubmission submission)
    {
        var key = SentKey(submission);
        if (cache.TryGetValue(key, out _))
            return;

        if (!_queue.Writer.TryWrite((title, submission)))
        {
            log.LogDebug("RemuxDB submission queue full, dropping {Filename}", submission.Filename);
            return;
        }

        cache.Set(key, true, SentTtl);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (title, submission) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (await IsKnownAsync(title, submission, stoppingToken).ConfigureAwait(false))
                {
                    log.LogDebug(
                        "RemuxDB already knows {Filename}, not submitting",
                        submission.Filename
                    );
                    continue;
                }

                if (await client.SubmitAsync(submission, stoppingToken).ConfigureAwait(false))
                {
                    log.LogInformation(
                        "Submitted the probe of {Filename} to RemuxDB",
                        submission.Filename
                    );
                    client.Forget(title);
                }
                else
                {
                    cache.Remove(SentKey(submission));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                cache.Remove(SentKey(submission));
                log.LogWarning(
                    "RemuxDB submission for {Filename} failed: {Error}",
                    submission.Filename,
                    ex.Message
                );
            }
        }
    }

    /// <summary>
    /// Whether RemuxDB already has this file of the torrent, in case another client submitted it
    /// since the streams were synced.
    /// </summary>
    private async Task<bool> IsKnownAsync(
        RemuxDbTitle title,
        RemuxDbSubmission submission,
        CancellationToken ct
    )
    {
        var versions = await client.GetVersionsAsync(title, ct).ConfigureAwait(false);
        return RemuxDbMapper.Match(
                versions,
                new StreamIdentity(
                    submission.TorrentInfoHash,
                    submission.TorrentFileIdx,
                    submission.Size,
                    submission.Filename
                )
            )
            is not null;
    }

    private static string SentKey(RemuxDbSubmission s) =>
        $"remuxdb-sent:{s.TorrentInfoHash}:{s.TorrentFileIdx}:{s.Size}";
}
