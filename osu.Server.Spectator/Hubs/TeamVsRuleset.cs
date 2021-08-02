// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchRulesets.TeamVs;

namespace osu.Server.Spectator.Hubs
{
    public class TeamVsRuleset : MatchRuleset
    {
        private readonly TeamVsMatchRoomState state;

        public TeamVsRuleset(ServerMultiplayerRoom room)
            : base(room)
        {
            room.MatchRulesetState = state = new TeamVsMatchRoomState
            {
                Teams =
                {
                    new MultiplayerTeam { ID = 0, Name = "Team Red" },
                    new MultiplayerTeam { ID = 1, Name = "Team Blue" },
                }
            };

            room.UpdateMatchRulesetRoomState(room);
        }

        public override void HandleUserJoined(MultiplayerRoomUser user)
        {
            base.HandleUserJoined(user);

            user.MatchRulesetState = new TeamVsMatchUserState { TeamID = getBestAvailableTeam() };
            Room.UpdateMatchRulesetUserState(Room, user);
        }

        public override void HandleUserRequest(MultiplayerRoomUser user, MatchRulesetUserRequest request)
        {
            switch (request)
            {
                case ChangeTeamRequest changeTeam:
                    if (state.Teams.All(t => t.ID != changeTeam.TeamID))
                        throw new InvalidStateException("Attempted to set team out of valid range");

                    if (user.MatchRulesetState is TeamVsMatchUserState userState)
                        userState.TeamID = changeTeam.TeamID;

                    Room.UpdateMatchRulesetUserState(Room, user);
                    break;
            }
        }

        /// <summary>
        /// For a user joining the room, this will provide the most appropriate team for the new user to keep the room balanced.
        /// </summary>
        private int getBestAvailableTeam()
        {
            // initially check for any teams which don't yet have players, but are lower than TeamCount.
            foreach (var team in state.Teams)
            {
                if (Room.Users.Count(u => (u.MatchRulesetState as TeamVsMatchUserState)?.TeamID == team.ID) == 0)
                    return team.ID;
            }

            var countsByTeams = Room.Users
                                    .GroupBy(u => (u.MatchRulesetState as TeamVsMatchUserState)?.TeamID)
                                    .Where(g => g.Key.HasValue)
                                    .OrderBy(g => g.Count());

            return countsByTeams.First().Key ?? 0;
        }
    }
}