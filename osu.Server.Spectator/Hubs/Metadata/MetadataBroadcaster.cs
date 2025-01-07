// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Timers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using osu.Game.Online.Metadata;
using osu.Server.Spectator.Database;
using Timer = System.Timers.Timer;

namespace osu.Server.Spectator.Hubs.Metadata
{
    /// <summary>
    /// A service which broadcasts any new metadata changes to <see cref="MetadataHub"/>.
    /// </summary>
    public class MetadataBroadcaster : IDisposable
    {
        private readonly IDatabaseFactory databaseFactory;
        private readonly IHubContext<MetadataHub> metadataHubContext;

        private readonly Timer timer;
        private readonly CancellationTokenSource timerCancellationSource;
        private readonly CancellationToken timerCancellationToken;
        private readonly ILogger logger;

        private int? lastQueueId;

        public MetadataBroadcaster(
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            IHubContext<MetadataHub> metadataHubContext)
        {
            this.databaseFactory = databaseFactory;
            this.metadataHubContext = metadataHubContext;
            this.logger = loggerFactory.CreateLogger(nameof(MetadataBroadcaster));

            timerCancellationSource = new CancellationTokenSource();
            timerCancellationToken = timerCancellationSource.Token;

            timer = new Timer(5000);
            timer.AutoReset = false;
            timer.Elapsed += pollForChanges;
            timer.Start();
        }

        // ReSharper disable once AsyncVoidMethod
        private async void pollForChanges(object? sender, ElapsedEventArgs args)
        {
            try
            {
                using (var db = databaseFactory.GetInstance())
                {
                    var updates = await db.GetUpdatedBeatmapSetsAsync(lastQueueId);

                    lastQueueId = updates.LastProcessedQueueID;
                    logger.LogInformation("Polled beatmap changes up to last queue id {lastProcessedQueueID}", updates.LastProcessedQueueID);

                    if (updates.BeatmapSetIDs.Any())
                    {
                        logger.LogInformation("Broadcasting new beatmaps to client: {beatmapIds}", string.Join(',', updates.BeatmapSetIDs.Select(i => i.ToString())));
                        await metadataHubContext.Clients.All.SendAsync(nameof(IMetadataClient.BeatmapSetsUpdated), updates, cancellationToken: timerCancellationToken);
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Error during beatmap update polling");
            }
            finally
            {
                if (timerCancellationToken.IsCancellationRequested)
                    timer.Dispose();
                else
                    timer.Start();
            }
        }

        public void Dispose()
        {
            timerCancellationSource.Cancel();
            timerCancellationSource.Dispose();
        }
    }
}
