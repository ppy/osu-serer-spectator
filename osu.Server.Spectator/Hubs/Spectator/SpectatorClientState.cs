// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Newtonsoft.Json;
using osu.Game.Online;
using osu.Game.Online.Spectator;
using osu.Game.Scoring;

namespace osu.Server.Spectator.Hubs.Spectator
{
    [Serializable]
    public class SpectatorClientState : ClientState
    {
        public SpectatorState? State;

        public Score? Score;

        public ScoreToken? ScoreToken;

        [JsonConstructor]
        public SpectatorClientState(in string connectionId, in int userId)
            : base(connectionId, userId)
        {
        }
    }
}
