// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database;

namespace osu.Server.Spectator.Hubs
{
    public class ServerMultiplayerRoom : MultiplayerRoom
    {
        private readonly IMultiplayerHubContext hub;

        private MatchTypeImplementation matchTypeImplementation;

        public MatchTypeImplementation MatchTypeImplementation
        {
            get => matchTypeImplementation;
            set
            {
                if (matchTypeImplementation == value)
                    return;

                matchTypeImplementation = value;

                foreach (var u in Users)
                    matchTypeImplementation.HandleUserJoined(u);
            }
        }

        public readonly MultiplayerQueue Queue;

        public ServerMultiplayerRoom(long roomId, IMultiplayerHubContext hub)
            : base(roomId)
        {
            this.hub = hub;

            // just to ensure non-null.
            matchTypeImplementation = createTypeImplementation(MatchType.HeadToHead);
            Queue = new MultiplayerQueue(this, hub);
        }

        public async Task Initialise(IDatabaseFactory dbFactory)
        {
            ChangeMatchType(Settings.MatchType);
            await Queue.Initialise(dbFactory);
        }

        /// <summary>
        /// Ensures that all states in this <see cref="ServerMultiplayerRoom"/> are valid to be newly serialised out to a client.
        /// </summary>
        public void UpdateForRetrieval()
        {
            if (Countdown != null)
            {
                DateTimeOffset countdownEnd = countdownStartTime + countdownDuration;
                TimeSpan timeRemaining = countdownEnd - DateTimeOffset.Now;

                Countdown.TimeRemaining = timeRemaining.TotalSeconds > 0 ? timeRemaining : TimeSpan.Zero;
            }
        }

        public void ChangeMatchType(MatchType type) => MatchTypeImplementation = createTypeImplementation(type);

        public void AddUser(MultiplayerRoomUser user)
        {
            Users.Add(user);
            MatchTypeImplementation.HandleUserJoined(user);
        }

        public void RemoveUser(MultiplayerRoomUser user)
        {
            Users.Remove(user);
            MatchTypeImplementation.HandleUserLeft(user);
        }

        private MatchTypeImplementation createTypeImplementation(MatchType type)
        {
            switch (type)
            {
                case MatchType.TeamVersus:
                    return new TeamVersus(this, hub);

                default:
                    return new HeadToHead(this, hub);
            }
        }

        #region Countdowns

        private CancellationTokenSource? countdownStopSource;
        private CancellationTokenSource? countdownSkipSource;
        private Task countdownTask = Task.CompletedTask;
        private TimeSpan countdownDuration;
        private DateTimeOffset countdownStartTime;

        /// <summary>
        /// Starts a new countdown, stopping any existing one.
        /// </summary>
        /// <param name="countdown">The countdown to start. The <see cref="MultiplayerRoom"/> will receive this object for the duration of the countdown.</param>
        /// <param name="onComplete">A callback to be invoked when the countdown completes.</param>
        public async Task StartCountdown(MultiplayerCountdown countdown, Func<ServerMultiplayerRoom, Task> onComplete)
        {
            await StopCountdown();

            var stopSource = countdownStopSource = new CancellationTokenSource();
            var skipSource = countdownSkipSource = new CancellationTokenSource();

            countdownStartTime = DateTimeOffset.Now;
            countdownDuration = countdown.TimeRemaining;

            Countdown = countdown;

            await hub.NotifyNewMatchEvent(this, new CountdownChangedEvent { Countdown = countdown });

            countdownTask = start();

            async Task start()
            {
                // Run the countdown.
                try
                {
                    using (var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(stopSource.Token, skipSource.Token))
                        await Task.Delay(countdownDuration, cancellationSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Clients need to be notified of cancellations in the following code.
                }

                // Notify users that the countdown has finished (or cancelled) and run the continuation.
                // Note: The room must be re-retrieved rather than using our own instance to enforce single-thread access.
                using (var roomUsage = await hub.GetRoom(RoomID))
                {
                    try
                    {
                        if (roomUsage.Item == null)
                            return;

                        if (stopSource.IsCancellationRequested)
                            return;

                        Debug.Assert(Countdown == countdown);

                        await StopCountdown();

                        // The continuation could be run outside of the room lock, however it seems saner to run it within the same lock as the cancellation token usage.
                        // Furthermore, providing a room-id instead of the room becomes cumbersome for usages, so this also provides a nicer API.
                        await onComplete(roomUsage.Item);
                    }
                    finally
                    {
                        stopSource.Dispose();
                        skipSource.Dispose();

                        // Although we are in a single-threaded context with regards to the construction/setters of the class-level fields,
                        // subsequent calls to StartCountdown() create new objects/override the class-level fields _before_ waiting on the previous countdown task to complete.
                        if (countdownStopSource == stopSource)
                            countdownStopSource = null;
                        if (countdownSkipSource == skipSource)
                            countdownSkipSource = null;
                    }
                }
            }
        }

        /// <summary>
        /// Stops any current countdown, preventing its callback from running.
        /// </summary>
        public async Task StopCountdown()
        {
            if (Countdown == null)
                return;

            countdownStopSource?.Cancel();
            Countdown = null;

            await hub.NotifyNewMatchEvent(this, new CountdownChangedEvent { Countdown = null });
        }

        /// <summary>
        /// Stops the current countdown if it's of the given type, preventing its callback from running.
        /// </summary>
        /// <typeparam name="T">The countdown type.</typeparam>
        public async Task StopCountdown<T>()
        {
            if (Countdown is T)
                await StopCountdown();
        }

        /// <summary>
        /// Skips to the end of the currently-running countdown, if one is running,
        /// and runs the callback (e.g. to start the match) as soon as possible unless the countdown has been cancelled.
        /// </summary>
        /// <returns>
        /// A task which will become completed when the active countdown completes. Make sure to await this *outside* a usage.
        /// </returns>
        public Task SkipToEndOfCountdown()
        {
            countdownSkipSource?.Cancel();
            return countdownTask;
        }

        public Task GetCurrentCountdownTask() => countdownTask;

        #endregion
    }
}
