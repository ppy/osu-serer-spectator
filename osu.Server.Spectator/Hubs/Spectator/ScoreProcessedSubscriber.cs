// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using osu.Game.Online.Spectator;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using StackExchange.Redis;
using StatsdClient;
using Timer = System.Timers.Timer;

namespace osu.Server.Spectator.Hubs.Spectator
{
    public sealed class ScoreProcessedSubscriber : IScoreProcessedSubscriber, IDisposable
    {
        /// <summary>
        /// The maximum amount of time to wait for a <see cref="ScoreProcessed"/> message for a given score in milliseconds.
        /// </summary>
        private const int timeout_interval_ms = 30_000;

        private const string statsd_prefix = "subscribers.score-processed";

        private readonly IDatabaseFactory databaseFactory;
        private readonly ISubscriber? subscriber;

        private readonly ConcurrentDictionary<long, SingleScoreSubscription> singleScoreSubscriptions = new ConcurrentDictionary<long, SingleScoreSubscription>();
        private readonly Timer timer;
        private readonly ILogger logger;
        private readonly IHubContext<SpectatorHub> spectatorHubContext;

        public ScoreProcessedSubscriber(
            IDatabaseFactory databaseFactory,
            IConnectionMultiplexer redis,
            IHubContext<SpectatorHub> spectatorHubContext,
            ILoggerFactory loggerFactory)
        {
            this.databaseFactory = databaseFactory;
            this.spectatorHubContext = spectatorHubContext;

            timer = new Timer(1000);
            timer.AutoReset = true;
            timer.Elapsed += (_, _) => Task.Run(purgeTimedOutSubscriptions);
            timer.Start();

            subscriber = redis.GetSubscriber();
            subscriber.Subscribe(new RedisChannel("osu-channel:score:processed", RedisChannel.PatternMode.Literal), (_, message) => onMessageReceived(message));

            logger = loggerFactory.CreateLogger(nameof(ScoreProcessedSubscriber));
        }

        private void onMessageReceived(string? message)
        {
            try
            {
                if (string.IsNullOrEmpty(message))
                    return;

                ScoreProcessed? scoreProcessed = JsonConvert.DeserializeObject<ScoreProcessed>(message);

                if (scoreProcessed == null)
                    return;

                if (singleScoreSubscriptions.TryRemove(scoreProcessed.ScoreId, out var subscription))
                {
                    using (subscription)
                        subscription.InvokeAsync().Wait();
                }

                DogStatsd.Increment($"{statsd_prefix}.messages.single-score.delivered");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process message");
                DogStatsd.Increment($"{statsd_prefix}.messages.single-score.dropped");
            }
        }

        public async Task RegisterForSingleScoreAsync(string receiverConnectionId, int userId, long scoreToken)
        {
            try
            {
                using var db = databaseFactory.GetInstance();

                SoloScore? score = await db.GetScoreFromToken(scoreToken);

                if (score == null)
                {
                    DogStatsd.Increment($"{statsd_prefix}.subscriptions.single-score.dropped");
                    return;
                }

                var subscription = new SingleScoreSubscription(receiverConnectionId, userId, (long)score.id, spectatorHubContext);

                // because the score submission flow happens concurrently with the spectator play finished flow,
                // it is theoretically possible for the score processing to complete before the spectator hub had a chance to register for notifications.
                // to cover off this possibility, check the database directly once.
                if (await db.IsScoreProcessedAsync((long)score.id))
                {
                    using (subscription)
                        await subscription.InvokeAsync();
                    DogStatsd.Increment($"{statsd_prefix}.messages.single-score.delivered-immediately");
                    return;
                }

                singleScoreSubscriptions.TryAdd((long)score.id, subscription);
                DogStatsd.Gauge($"{statsd_prefix}.subscriptions.single-score.total", singleScoreSubscriptions.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register connection {receiverConnectionId} for info about score {userId}:{scoreToken}",
                    receiverConnectionId,
                    userId,
                    scoreToken);
                DogStatsd.Increment($"{statsd_prefix}.subscriptions.single-score.failed");
            }
        }

        private void purgeTimedOutSubscriptions()
        {
            var scoreIds = singleScoreSubscriptions.Keys.ToArray();
            int purgedCount = 0;

            foreach (var scoreId in scoreIds)
            {
                if (singleScoreSubscriptions.TryGetValue(scoreId, out var subscription) && subscription.TimedOut)
                {
                    subscription.Dispose();

                    if (singleScoreSubscriptions.TryRemove(scoreId, out _))
                        purgedCount += 1;
                }
            }

            if (purgedCount > 0)
            {
                DogStatsd.Gauge($"{statsd_prefix}.subscriptions.single-score.total", singleScoreSubscriptions.Count);
                DogStatsd.Increment($"{statsd_prefix}.subscriptions.single-score.timed-out", purgedCount);
            }

            if (!disposed)
                timer.Start();
        }

        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            subscriber?.UnsubscribeAll();
        }

        private record ScoreProcessed(long ScoreId);

        private class SingleScoreSubscription : IDisposable
        {
            private readonly string receiverConnectionId;
            private readonly int userId;
            private readonly long scoreId;
            private readonly IHubContext<SpectatorHub> spectatorHubContext;

            private readonly CancellationTokenSource cancellationTokenSource;
            public bool TimedOut => cancellationTokenSource.IsCancellationRequested;

            public SingleScoreSubscription(string receiverConnectionId, int userId, long scoreId, IHubContext<SpectatorHub> spectatorHubContext)
            {
                this.receiverConnectionId = receiverConnectionId;
                this.userId = userId;
                this.scoreId = scoreId;
                this.spectatorHubContext = spectatorHubContext;

                cancellationTokenSource = new CancellationTokenSource(timeout_interval_ms);
            }

            public Task InvokeAsync()
                => spectatorHubContext.Clients.Client(receiverConnectionId).SendAsync(nameof(ISpectatorClient.UserScoreProcessed), userId, scoreId);

            public void Dispose() => cancellationTokenSource.Dispose();
        }
    }
}
