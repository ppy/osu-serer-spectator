// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs;
using osu.Server.Spectator.Hubs.Spectator;
using osu.Server.Spectator.Storage;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class SpectatorHubTest
    {
        private readonly SpectatorHub hub;

        private const int streamer_id = 1234;
        private const string streamer_username = "user";
        private const int beatmap_id = 88;
        private const int watcher_id = 8000;

        private readonly ScoreUploader scoreUploader;
        private readonly Mock<IScoreStorage> mockScoreStorage;
        private readonly Mock<IDatabaseAccess> mockDatabase;

        public SpectatorHubTest()
        {
            var clientStates = new EntityStore<SpectatorClientState>();
            var spectatorLists = new EntityStore<SpectatorList>();

            mockDatabase = new Mock<IDatabaseAccess>();
            mockDatabase.Setup(db => db.GetUsernameAsync(streamer_id)).ReturnsAsync(() => streamer_username);

            mockDatabase.Setup(db => db.GetBeatmapAsync(It.IsAny<int>()))
                        .ReturnsAsync((int id) => new database_beatmap
                        {
                            approved = BeatmapOnlineStatus.Ranked,
                            checksum = (id == beatmap_id ? "d2a97fb2fa4529a5e857fe0466dc1daf" : string.Empty)
                        });

            var databaseFactory = new Mock<IDatabaseFactory>();
            databaseFactory.Setup(factory => factory.GetInstance()).Returns(mockDatabase.Object);

            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>()))
                         .Returns(new Mock<ILogger>().Object);

            mockScoreStorage = new Mock<IScoreStorage>();
            scoreUploader = new ScoreUploader(loggerFactory.Object, databaseFactory.Object, mockScoreStorage.Object);

            var mockScoreProcessedSubscriber = new Mock<IScoreProcessedSubscriber>();

            hub = new SpectatorHub(loggerFactory.Object, clientStates, spectatorLists, databaseFactory.Object, scoreUploader, mockScoreProcessedSubscriber.Object);
        }

        [Fact]
        public async Task NewUserConnectsAndStreamsData()
        {
            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            await hub.BeginPlaySession(0, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            // check all other users were informed that streaming began
            mockClients.Verify(clients => clients.All, Times.Once);
            mockReceiver.Verify(clients => clients.UserBeganPlaying(streamer_id, It.Is<SpectatorState>(m => m.Equals(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            }))), Times.Once());

            var data = new FrameDataBundle(
                new FrameHeader(new ScoreInfo(), new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) });

            // check streaming data is propagating to watchers
            await hub.SendFrameData(data);
            mockReceiver.Verify(clients => clients.UserSentFrames(streamer_id, data));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReplayDataIsSaved(bool savingEnabled)
        {
            scoreUploader.SaveReplays = savingEnabled;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = true
            }));

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            await hub.SendFrameData(new FrameDataBundle(
                new FrameHeader(new ScoreInfo
                {
                    Statistics =
                    {
                        [HitResult.Great] = 1
                    }
                }, new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Passed,
            });

            await uploadsCompleteAsync();

            if (savingEnabled)
                mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 456)), Times.Once);
            else
                mockScoreStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Never);

            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Passed)), Times.Once());
        }

        [Fact]
        public async Task ReplaysWithoutAnyHitsAreDiscarded()
        {
            scoreUploader.SaveReplays = true;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = true
            }));

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            await hub.SendFrameData(new FrameDataBundle(
                new FrameHeader(new ScoreInfo(), new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Quit,
            });

            await uploadsCompleteAsync();

            mockScoreStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Never);
            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Quit)), Times.Once());
        }

        [Fact]
        public async Task ModChangesDuringPlayAreHandled()
        {
            scoreUploader.SaveReplays = true;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = true
            }));

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            await hub.SendFrameData(new FrameDataBundle(
                new FrameHeader(new ScoreInfo
                {
                    Mods = [new OsuModTouchDevice()],
                    Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 1 }
                }, new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Quit,
            });

            await uploadsCompleteAsync();

            mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(s => s.ScoreInfo.Mods.Any(m => m is OsuModTouchDevice))), Times.Once);
            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Quit)), Times.Once());
        }

        [Fact]
        public async Task FrameBundlesFromOldClientsWithoutModsHandledCorrectly()
        {
            scoreUploader.SaveReplays = true;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = true
            }));

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
                Mods = [new APIMod(new OsuModDoubleTime())]
            });

            var frameHeader = new FrameHeader(new ScoreInfo
            {
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 1 }
            }, new ScoreProcessorStatistics())
            {
                Mods = null, // simulate older client that did not send this property over wire
            };

            await hub.SendFrameData(new FrameDataBundle(
                frameHeader,
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Quit,
            });

            await uploadsCompleteAsync();

            mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(s => s.ScoreInfo.Mods.Any(m => m is OsuModDoubleTime))), Times.Once);
            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Quit)), Times.Once());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NewUserBeginsWatchingStream(bool ongoing)
        {
            string connectionId = Guid.NewGuid().ToString();

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockCaller = new Mock<ISpectatorClient>();
            Mock<ISpectatorClient> mockStreamer = new Mock<ISpectatorClient>();

            mockClients.Setup(clients => clients.Caller).Returns(mockCaller.Object);
            mockClients.Setup(clients => clients.All).Returns(mockCaller.Object);
            mockClients.Setup(clients => clients.User(streamer_id.ToString())).Returns(mockStreamer.Object);
            mockDatabase.Setup(db => db.GetUsernameAsync(watcher_id)).ReturnsAsync("watcher");

            Mock<IGroupManager> mockGroups = new Mock<IGroupManager>();

            Mock<HubCallerContext> streamerContext = new Mock<HubCallerContext>();
            streamerContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());

            Mock<HubCallerContext> watcherContext = new Mock<HubCallerContext>();
            watcherContext.Setup(context => context.UserIdentifier).Returns(watcher_id.ToString());
            watcherContext.Setup(context => context.ConnectionId).Returns(connectionId);

            hub.Clients = mockClients.Object;
            hub.Groups = mockGroups.Object;

            var state = new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
            };

            if (ongoing)
            {
                hub.Context = streamerContext.Object;
                await hub.BeginPlaySession(0, state);

                mockCaller.Verify(clients => clients.UserBeganPlaying(streamer_id, It.Is<SpectatorState>(m => m.Equals(state))), Times.Once);
            }

            hub.Context = watcherContext.Object;

            await hub.StartWatchingUser(streamer_id);

            mockGroups.Verify(groups => groups.AddToGroupAsync(connectionId, SpectatorHub.GetGroupId(streamer_id), default));

            mockCaller.Verify(clients => clients.UserBeganPlaying(streamer_id, It.Is<SpectatorState>(m => m.Equals(state))), Times.Exactly(ongoing ? 2 : 0));
            mockStreamer.Verify(client => client.UserStartedWatching(It.Is<SpectatorUser[]>(users => users.Single().OnlineID == watcher_id)), Times.Once);

            await hub.EndWatchingUser(streamer_id);

            mockGroups.Verify(groups => groups.RemoveFromGroupAsync(connectionId, SpectatorHub.GetGroupId(streamer_id), default));
            mockStreamer.Verify(client => client.UserEndedWatching(watcher_id), Times.Once);
        }

        [Fact]
        public async Task MaliciousUserCannotFinishWithPlayingState()
        {
            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            // Begin play.
            await hub.BeginPlaySession(0, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing
            });

            // End play, but set a playing state.
            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing
            });

            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Quit)), Times.Once());
        }

        [Fact]
        public async Task DisconnectedUserSendsQuitState()
        {
            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            // Begin play.
            await hub.BeginPlaySession(0, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing
            });

            // Forcefully terminate the connection.
            await hub.OnDisconnectedAsync(new Exception());

            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Quit)), Times.Once());
        }

        [Theory]
        [InlineData(BeatmapOnlineStatus.LocallyModified, false)]
        [InlineData(BeatmapOnlineStatus.None, false)]
        [InlineData(BeatmapOnlineStatus.Graveyard, false)]
        [InlineData(BeatmapOnlineStatus.WIP, false)]
        [InlineData(BeatmapOnlineStatus.Pending, false)]
        [InlineData(BeatmapOnlineStatus.Ranked, true)]
        [InlineData(BeatmapOnlineStatus.Approved, true)]
        [InlineData(BeatmapOnlineStatus.Qualified, true)]
        [InlineData(BeatmapOnlineStatus.Loved, true)]
        public async Task ScoresAreOnlySavedOnRankedBeatmaps(BeatmapOnlineStatus status, bool saved)
        {
            scoreUploader.SaveReplays = true;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = true
            }));

            mockDatabase.Setup(db => db.GetBeatmapAsync(beatmap_id)).Returns(Task.FromResult(new database_beatmap
            {
                approved = status,
                checksum = "checksum"
            })!);

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            await hub.SendFrameData(new FrameDataBundle(
                new FrameHeader(new ScoreInfo
                {
                    Statistics =
                    {
                        [HitResult.Great] = 10
                    }
                }, new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Passed,
            });

            await uploadsCompleteAsync();

            if (saved)
                mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 456)), Times.Once);
            else
                mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 456)), Times.Never);

            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Passed)), Times.Once());
        }

        /// <summary>
        /// This test is very stupid.
        /// It is very stupid because <see cref="ScoreInfo"/> is playing very stupid games,
        /// and thus the goal of this test is to be stupid to prevent other people from doing stupid things accidentally.
        /// To expound on the above: <see cref="ScoreInfo"/> has two user-like properties: <see cref="ScoreInfo.User"/> and <see cref="ScoreInfo.RealmUser"/>.
        /// If you looked at their source, you'd say that they're supposed to be kept in sync, right?
        /// Well, not necessarily, because nested object initialiser syntax exists, and therefore you can write something like
        /// <code>
        /// new ScoreInfo
        /// {
        ///     User =
        ///     {
        ///         OnlineID = 1234,
        ///         Username = "test",
        ///     }
        /// }
        /// </code>
        /// and it does <b>not</b> call the setter of <see cref="ScoreInfo.User"/>.
        /// It accesses the <b>underlying <see cref="APIUser"/> instance under <see cref="ScoreInfo.User"/></b> and sets things on it directly.
        /// An additional badness is that <see cref="ScoreInfo.UserID"/> looks innocuous but in fact goes through <see cref="ScoreInfo.RealmUser"/>,
        /// which means that if anything (like the replay encoder) uses it, it will silently read broken garbage.
        /// Until this is sorted, this test attempts to exercise at least a modicum of sanity,
        /// so that at least red lights show up if anything funny is going on with these accursed models.
        /// </summary>
        [Fact]
        public async Task ScoresHaveAllUserRelatedMetadataFilledOutConsistently()
        {
            scoreUploader.SaveReplays = true;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = true
            }));

            mockDatabase.Setup(db => db.GetBeatmapAsync(beatmap_id)).Returns(Task.FromResult(new database_beatmap
            {
                approved = BeatmapOnlineStatus.Ranked,
                checksum = "checksum"
            })!);

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            await hub.SendFrameData(new FrameDataBundle(
                new FrameHeader(new ScoreInfo
                {
                    Statistics =
                    {
                        [HitResult.Great] = 10
                    }
                }, new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Passed,
            });

            await uploadsCompleteAsync();

            mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.UserID == streamer_id
                                                                            && score.ScoreInfo.User.OnlineID == streamer_id
                                                                            && score.ScoreInfo.User.Username == streamer_username
                                                                            && score.ScoreInfo.RealmUser.OnlineID == streamer_id
                                                                            && score.ScoreInfo.RealmUser.Username == streamer_username)), Times.Once);

            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Passed)), Times.Once());
        }

        [Fact]
        public async Task FailedScoresAreNotSaved()
        {
            scoreUploader.SaveReplays = true;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = false
            }));

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            await hub.SendFrameData(new FrameDataBundle(
                new FrameHeader(new ScoreInfo(), new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Failed,
            });

            await uploadsCompleteAsync();

            mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 456)), Times.Never);
            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Failed)), Times.Once());
        }

        [Fact]
        public async Task ScoreRankPopulatedCorrectly()
        {
            scoreUploader.SaveReplays = true;

            Mock<IHubCallerClients<ISpectatorClient>> mockClients = new Mock<IHubCallerClients<ISpectatorClient>>();
            Mock<ISpectatorClient> mockReceiver = new Mock<ISpectatorClient>();
            mockClients.Setup(clients => clients.All).Returns(mockReceiver.Object);
            mockClients.Setup(clients => clients.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(mockReceiver.Object);

            Mock<HubCallerContext> mockContext = new Mock<HubCallerContext>();

            mockContext.Setup(context => context.UserIdentifier).Returns(streamer_id.ToString());
            hub.Context = mockContext.Object;
            hub.Clients = mockClients.Object;

            mockDatabase.Setup(db => db.GetScoreFromTokenAsync(1234)).Returns(Task.FromResult<SoloScore?>(new SoloScore
            {
                id = 456,
                passed = true
            }));

            await hub.BeginPlaySession(1234, new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Playing,
            });

            await hub.SendFrameData(new FrameDataBundle(
                new FrameHeader(new ScoreInfo
                {
                    Accuracy = 0.95,
                    Statistics = new Dictionary<HitResult, int>
                    {
                        [HitResult.Great] = 19,
                        [HitResult.Miss] = 1,
                    }
                }, new ScoreProcessorStatistics()),
                new[] { new LegacyReplayFrame(1234, 0, 0, ReplayButtonState.None) }));

            await hub.EndPlaySession(new SpectatorState
            {
                BeatmapID = beatmap_id,
                RulesetID = 0,
                State = SpectatedUserState.Passed,
            });

            await uploadsCompleteAsync();

            mockScoreStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.Rank == ScoreRank.A)), Times.Once);
            mockReceiver.Verify(clients => clients.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(m => m.State == SpectatedUserState.Passed)), Times.Once());
        }

        private async Task uploadsCompleteAsync(int attempts = 5)
        {
            while (scoreUploader.RemainingUsages > 0)
            {
                if (attempts <= 0)
                    Assert.Fail("Waiting for score upload to proceed timed out");

                attempts -= 1;
                await Task.Delay(1000);
            }
        }
    }
}
