// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using osu.Game.Online;
using osu.Game.Scoring;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Hubs;
using osu.Server.Spectator.Storage;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class ScoreUploaderTests
    {
        private readonly ScoreUploader uploader;
        private readonly Mock<IDatabaseAccess> mockDatabase;
        private readonly Mock<IScoreStorage> mockStorage;

        public ScoreUploaderTests()
        {
            mockDatabase = new Mock<IDatabaseAccess>();
            mockDatabase.Setup(db => db.GetScoreIdFromToken(new ScoreToken(1, ScoreTokenType.Solo)))
                        .Returns(Task.FromResult<long?>(2));

            var databaseFactory = new Mock<IDatabaseFactory>();
            databaseFactory.Setup(factory => factory.GetInstance()).Returns(mockDatabase.Object);

            mockStorage = new Mock<IScoreStorage>();
            uploader = new ScoreUploader(databaseFactory.Object, mockStorage.Object);
            uploader.UploadInterval = 1000; // Set a high timer interval for testing purposes.
        }

        [Fact]
        public async Task ScoreUploadsEveryInterval()
        {
            enableUpload();

            // First score.
            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            await Task.Delay(2000);
            mockStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 2)), Times.Once);

            // Second score (ensure the loop keeps running).
            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            await Task.Delay(2000);
            mockStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 2)), Times.Exactly(2));
        }

        [Fact]
        public async Task ScoreDoesNotUploadIfDisabled()
        {
            disableUpload();

            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Never);
        }

        [Fact]
        public async Task ScoreOnlyUploadsOnce()
        {
            enableUpload();

            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            await uploader.Flush();
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Once);
        }

        [Fact]
        public async Task ScoreUploadsWithDelayedScoreToken()
        {
            enableUpload();

            // Score with no token.
            uploader.Enqueue(new ScoreToken(2, ScoreTokenType.Solo), new Score());
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Never);

            // Give the score a token.
            mockDatabase.Setup(db => db.GetScoreIdFromToken(new ScoreToken(2, ScoreTokenType.Solo)))
                        .Returns(Task.FromResult<long?>(3));
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 3)), Times.Once);
        }

        [Fact]
        public async Task TimedOutScoreDoesNotUpload()
        {
            enableUpload();

            uploader.TimeoutInterval = 0;

            // Score with no token.
            uploader.Enqueue(new ScoreToken(2, ScoreTokenType.Solo), new Score());
            Thread.Sleep(1000); // Wait for cancellation.
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Never);

            // Give the score a token now. It should still not upload because it has timed out.
            mockDatabase.Setup(db => db.GetScoreIdFromToken(new ScoreToken(2, ScoreTokenType.Solo)))
                        .Returns(Task.FromResult<long?>(3));
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Never);

            // New score that has a token (ensure the loop keeps running).
            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.IsAny<Score>()), Times.Once);
            mockStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 2)), Times.Once);
        }

        [Fact]
        public async Task FailedScoreHandledGracefully()
        {
            enableUpload();

            bool shouldThrow = true;
            int uploadCount = 0;

            mockStorage.Setup(storage => storage.WriteAsync(It.IsAny<Score>()))
                       .Callback<Score>(_ =>
                       {
                           // ReSharper disable once AccessToModifiedClosure
                           if (shouldThrow)
                               throw new InvalidOperationException();

                           uploadCount++;
                       });

            // Throwing score.
            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            await uploader.Flush();
            Assert.Equal(0, uploadCount);

            shouldThrow = false;

            // Same score shouldn't reupload.
            await uploader.Flush();
            Assert.Equal(0, uploadCount);

            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            await uploader.Flush();
            Assert.Equal(1, uploadCount);
        }

        [Fact]
        public async Task TimedOutItemGetsOneAttempt()
        {
            enableUpload();

            uploader.TimeoutInterval = 0;

            // Score with no token.
            uploader.Enqueue(new ScoreToken(1, ScoreTokenType.Solo), new Score());
            Thread.Sleep(1000); // Wait for cancellation.
            await uploader.Flush();
            mockStorage.Verify(s => s.WriteAsync(It.Is<Score>(score => score.ScoreInfo.OnlineID == 2)), Times.Once);
        }

        private void enableUpload() => AppSettings.SaveReplays = true;
        private void disableUpload() => AppSettings.SaveReplays = false;
    }
}
