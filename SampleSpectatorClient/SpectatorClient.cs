// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using osu.Game.Online;
using osu.Game.Online.Spectator;

namespace SampleSpectatorClient
{
    public class SpectatorClient : ISpectatorClient
    {
        private readonly HubConnection connection;

        private readonly List<int> watchingUsers = new List<int>();

        public SpectatorClient(HubConnection connection)
        {
            this.connection = connection;

            // this is kind of SILLY
            // https://github.com/dotnet/aspnetcore/issues/15198
            connection.On<int, SpectatorState>(nameof(ISpectatorClient.UserBeganPlaying), ((ISpectatorClient)this).UserBeganPlaying);
            connection.On<int, FrameDataBundle>(nameof(ISpectatorClient.UserSentFrames), ((ISpectatorClient)this).UserSentFrames);
            connection.On<int, SpectatorState>(nameof(ISpectatorClient.UserFinishedPlaying), ((ISpectatorClient)this).UserFinishedPlaying);
            connection.On<int, long>(nameof(ISpectatorClient.UserScoreProcessed), ((ISpectatorClient)this).UserScoreProcessed);
            connection.On(nameof(IStatefulUserHubClient.DisconnectRequested), ((IStatefulUserHubClient)this).DisconnectRequested);
        }

        Task ISpectatorClient.UserBeganPlaying(int userId, SpectatorState state)
        {
            if (watchingUsers.Contains(userId))
            {
                Console.WriteLine($"{connection.ConnectionId} received began playing for already watched user {userId}");
            }
            else
            {
                Console.WriteLine($"{connection.ConnectionId} requesting watch other user {userId}");
                WatchUser(userId);
                watchingUsers.Add(userId);
            }

            return Task.CompletedTask;
        }

        Task ISpectatorClient.UserFinishedPlaying(int userId, SpectatorState state)
        {
            Console.WriteLine($"{connection.ConnectionId} Received user finished event {state}");
            return Task.CompletedTask;
        }

        Task ISpectatorClient.UserSentFrames(int userId, FrameDataBundle data)
        {
            Console.WriteLine($"{connection.ConnectionId} Received frames from {userId}: {data.Frames.First()}");
            return Task.CompletedTask;
        }

        Task ISpectatorClient.UserScoreProcessed(int userId, long scoreId)
        {
            Console.WriteLine($"{connection.ConnectionId} Processing score with ID {scoreId} for player {userId} completed");
            return Task.CompletedTask;
        }

        Task ISpectatorClient.UserStartedWatching(SpectatorUser[] users)
        {
            foreach (var user in users)
                Console.WriteLine($"{connection.ConnectionId} User {user.OnlineID} started watching you");
            return Task.CompletedTask;
        }

        Task ISpectatorClient.UserEndedWatching(int userId)
        {
            Console.WriteLine($"{connection.ConnectionId} User {userId} ended watching you");
            return Task.CompletedTask;
        }

        public Task BeginPlaying(long? scoreToken, SpectatorState state) => connection.SendAsync(nameof(ISpectatorServer.BeginPlaySession), scoreToken, state);

        public Task SendFrames(FrameDataBundle data) => connection.SendAsync(nameof(ISpectatorServer.SendFrameData), data);

        public Task EndPlaying(SpectatorState state) => connection.SendAsync(nameof(ISpectatorServer.EndPlaySession), state);

        public Task WatchUser(int userId) => connection.SendAsync(nameof(ISpectatorServer.StartWatchingUser), userId);

        public Task DisconnectRequested()
        {
            Console.WriteLine($"{connection.ConnectionId} Disconnect requested");
            return Task.CompletedTask;
        }
    }
}
