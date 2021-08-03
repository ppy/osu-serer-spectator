// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Utils;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Hubs
{
    public class MultiplayerHub : StatefulUserHub<IMultiplayerClient, MultiplayerClientState>, IMultiplayerServer, IMultiplayerServerMatchCallbacks
    {
        protected readonly EntityStore<ServerMultiplayerRoom> Rooms;

        private readonly IDatabaseFactory databaseFactory;

        public MultiplayerHub(IDistributedCache cache, EntityStore<ServerMultiplayerRoom> rooms, EntityStore<MultiplayerClientState> users, IDatabaseFactory databaseFactory)
            : base(cache, users)
        {
            Rooms = rooms;
            this.databaseFactory = databaseFactory;
        }

        public Task<MultiplayerRoom> JoinRoom(long roomId) => JoinRoomWithPassword(roomId, string.Empty);

        public async Task<MultiplayerRoom> JoinRoomWithPassword(long roomId, string password)
        {
            Log($"Joining room {roomId}");

            bool isRestricted;
            using (var db = databaseFactory.GetInstance())
                isRestricted = await db.IsUserRestrictedAsync(CurrentContextUserId);

            if (isRestricted)
                throw new InvalidStateException("Can't join a room when restricted.");

            using (var userUsage = await GetOrCreateLocalUserState())
            {
                if (userUsage.Item != null)
                {
                    // if the user already has a state, it means they are already in a room and can't join another without first leaving.
                    throw new InvalidStateException("Can't join a room when already in another room.");
                }

                // add the user to the room.
                var roomUser = new MultiplayerRoomUser(CurrentContextUserId);

                // track whether this join necessitated starting the process of fetching the room and adding it to the room store.
                bool newRoomFetchStarted = false;

                ServerMultiplayerRoom? room = null;

                using (var roomUsage = await Rooms.GetForUse(roomId, true))
                {
                    try
                    {
                        if (roomUsage.Item == null)
                        {
                            newRoomFetchStarted = true;

                            // the requested room is not yet tracked by this server.
                            room = await retrieveRoom(roomId);

                            if (!string.IsNullOrEmpty(room.Settings.Password))
                            {
                                if (room.Settings.Password != password)
                                    throw new InvalidPasswordException();
                            }

                            // the above call will only succeed if this user is the host.
                            room.Host = roomUser;

                            // mark the room active - and wait for confirmation of this operation from the database - before adding the user to the room.
                            await markRoomActive(room);

                            roomUsage.Item = room;
                        }
                        else
                        {
                            room = roomUsage.Item;

                            // this is a sanity check to keep *rooms* in a good state.
                            // in theory the connection clean-up code should handle this correctly.
                            if (room.Users.Any(u => u.UserID == roomUser.UserID))
                                throw new InvalidOperationException($"User {roomUser.UserID} attempted to join room {room.RoomID} they are already present in.");
                        }

                        userUsage.Item = new MultiplayerClientState(Context.ConnectionId, CurrentContextUserId, roomId);

                        // because match type implementations may send subsequent information via Users collection hooks,
                        // inform clients before adding user to the room.
                        await Clients.Group(GetGroupId(roomId)).UserJoined(roomUser);

                        room.Users.Add(roomUser);

                        await addDatabaseUser(room, roomUser);

                        await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupId(roomId));

                        Log($"Joined room {room.RoomID}");
                    }
                    catch
                    {
                        try
                        {
                            if (userUsage.Item != null)
                            {
                                // the user was joined to the room, so we can run the standard leaveRoom method.
                                // this will handle closing the room if this was the only user.
                                await leaveRoom(userUsage.Item, roomUsage);
                            }
                            else if (newRoomFetchStarted)
                            {
                                if (room != null)
                                {
                                    // the room was retrieved and associated to the usage, but something failed before the user (host) could join.
                                    // for now, let's mark the room as ended if this happens.
                                    await endDatabaseMatch(room);
                                }

                                roomUsage.Destroy();
                            }
                        }
                        finally
                        {
                            // no matter how we end up cleaning up the room, ensure the user's context is destroyed.
                            userUsage.Destroy();
                        }

                        throw;
                    }
                }

                var settings = new JsonSerializerSettings
                {
                    // explicitly use Auto here as we are not interested in the top level type being conveyed to the user.
                    TypeNameHandling = TypeNameHandling.Auto,
                };

                return JsonConvert.DeserializeObject<MultiplayerRoom>(JsonConvert.SerializeObject(room, settings), settings)
                       ?? throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// Attempt to retrieve and construct a room from the database backend, based on a room ID specification.
        /// This will check the database backing to ensure things are in a consistent state.
        /// It should only be called by the room's host, before any other user has joined (and will throw if not).
        /// </summary>
        /// <param name="roomId">The proposed room ID.</param>
        /// <exception cref="InvalidStateException">If anything is wrong with this request.</exception>
        private async Task<ServerMultiplayerRoom> retrieveRoom(long roomId)
        {
            Log($"Retrieving room {roomId} from database");

            using (var db = databaseFactory.GetInstance())
            {
                var databaseRoom = await db.GetRoomAsync(roomId);

                if (databaseRoom == null)
                    throw new InvalidStateException("Specified match does not exist.");

                if (databaseRoom.ends_at != null && databaseRoom.ends_at < DateTimeOffset.Now)
                    throw new InvalidStateException("Match has already ended.");

                if (databaseRoom.user_id != CurrentContextUserId)
                    throw new InvalidStateException("Non-host is attempting to join match before host");

                var playlistItem = await db.GetCurrentPlaylistItemAsync(roomId);
                var beatmapChecksum = await db.GetBeatmapChecksumAsync(playlistItem.beatmap_id);

                if (beatmapChecksum == null)
                    throw new InvalidOperationException($"Expected non-null checksum on beatmap ID {playlistItem.beatmap_id}");

                var requiredMods = JsonConvert.DeserializeObject<IEnumerable<APIMod>>(playlistItem.required_mods ?? string.Empty) ?? Array.Empty<APIMod>();
                var allowedMods = JsonConvert.DeserializeObject<IEnumerable<APIMod>>(playlistItem.allowed_mods ?? string.Empty) ?? Array.Empty<APIMod>();

                return new ServerMultiplayerRoom(roomId, this)
                {
                    Settings = new MultiplayerRoomSettings
                    {
                        BeatmapChecksum = beatmapChecksum,
                        BeatmapID = playlistItem.beatmap_id,
                        RulesetID = playlistItem.ruleset_id,
                        Name = databaseRoom.name,
                        Password = databaseRoom.password,
                        RequiredMods = requiredMods,
                        AllowedMods = allowedMods,
                        PlaylistItemId = playlistItem.id,
                        MatchType = databaseRoom.type
                    }
                };
            }
        }

        /// <summary>
        /// Marks a room active at the database, implying the host has joined and this server is now in control of the room's lifetime.
        /// </summary>
        private async Task markRoomActive(MultiplayerRoom room)
        {
            Log($"Host marking room active {room.RoomID}");

            using (var db = databaseFactory.GetInstance())
                await db.MarkRoomActiveAsync(room);
        }

        public async Task LeaveRoom()
        {
            Log("Requesting to leave room");

            using (var userUsage = await GetOrCreateLocalUserState())
            {
                if (userUsage.Item == null)
                    throw new NotJoinedRoomException();

                try
                {
                    await leaveRoom(userUsage.Item);
                }
                finally
                {
                    userUsage.Destroy();
                }
            }
        }

        public async Task TransferHost(int userId)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                ensureIsHost(room);

                var newHost = room.Users.FirstOrDefault(u => u.UserID == userId);

                if (newHost == null)
                    throw new Exception("Target user is not in the current room");

                await setNewHost(room, newHost);
            }
        }

        public async Task ChangeState(MultiplayerUserState newState)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                var user = room.Users.FirstOrDefault(u => u.UserID == CurrentContextUserId);

                if (user == null)
                    throw new InvalidStateException("Local user was not found in the expected room");

                if (user.State == newState)
                    return;

                ensureValidStateSwitch(room, user.State, newState);
                user.State = newState;

                // handle whether this user should be receiving gameplay messages or not.
                switch (newState)
                {
                    case MultiplayerUserState.FinishedPlay:
                    case MultiplayerUserState.Idle:
                        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupId(room.RoomID, true));
                        break;

                    case MultiplayerUserState.Ready:
                    case MultiplayerUserState.Spectating:
                        await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupId(room.RoomID, true));
                        break;
                }

                await Clients.Group(GetGroupId(room.RoomID)).UserStateChanged(CurrentContextUserId, newState);

                if (newState == MultiplayerUserState.Spectating
                    && (room.State == MultiplayerRoomState.WaitingForLoad || room.State == MultiplayerRoomState.Playing))
                {
                    await Clients.Caller.LoadRequested();
                }

                await updateRoomStateIfRequired(room);
            }
        }

        public async Task ChangeBeatmapAvailability(BeatmapAvailability newBeatmapAvailability)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                var user = room.Users.FirstOrDefault(u => u.UserID == CurrentContextUserId);

                if (user == null)
                    throw new InvalidOperationException("Local user was not found in the expected room");

                if (user.BeatmapAvailability.Equals(newBeatmapAvailability))
                    return;

                user.BeatmapAvailability = newBeatmapAvailability;

                await Clients.Group(GetGroupId(room.RoomID)).UserBeatmapAvailabilityChanged(CurrentContextUserId, newBeatmapAvailability);
            }
        }

        public async Task ChangeUserMods(IEnumerable<APIMod> newMods)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                var user = room.Users.FirstOrDefault(u => u.UserID == CurrentContextUserId);

                if (user == null)
                    throw new InvalidOperationException("Local user was not found in the expected room");

                await changeUserMods(newMods, room, user);
            }
        }

        public async Task SendMatchRequest(MatchUserRequest request)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                var user = room.Users.FirstOrDefault(u => u.UserID == CurrentContextUserId);

                if (user == null)
                    throw new InvalidOperationException("Local user was not found in the expected room");

                room.MatchTypeImplementation.HandleUserRequest(user, request);
            }
        }

        public async Task StartMatch()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                ensureIsHost(room);

                if (room.State != MultiplayerRoomState.Open)
                    throw new InvalidStateException("Can't start match when already in a running state.");

                var readyUsers = room.Users.Where(u => u.State == MultiplayerUserState.Ready).ToArray();

                if (readyUsers.Length == 0)
                    throw new InvalidStateException("Can't start match when no users are ready.");

                if (room.Host != null && room.Host.State != MultiplayerUserState.Spectating && room.Host.State != MultiplayerUserState.Ready)
                    throw new InvalidStateException("Can't start match when the host is not ready.");

                foreach (var u in readyUsers)
                    await changeAndBroadcastUserState(room, u, MultiplayerUserState.WaitingForLoad);

                await changeRoomState(room, MultiplayerRoomState.WaitingForLoad);

                await Clients.Group(GetGroupId(room.RoomID, true)).LoadRequested();
            }
        }

        public async Task ChangeSettings(MultiplayerRoomSettings settings)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                if (room.State != MultiplayerRoomState.Open)
                    throw new InvalidStateException("Attempted to change settings while game is active");

                ensureIsHost(room);

                // Server is authoritative over the playlist item ID.
                // Todo: This needs to change for tournament mode.
                settings.PlaylistItemId = room.Settings.PlaylistItemId;

                if (room.Settings.Equals(settings))
                    return;

                var previousSettings = room.Settings;

                if (previousSettings.MatchType != settings.MatchType)
                {
                    room.MatchTypeImplementation = getTypeImplementation(settings, room);
                    Log($"Switching room ruleset to {room.MatchTypeImplementation}");
                }

                if (settings.RulesetID < 0 || settings.RulesetID > ILegacyRuleset.MAX_LEGACY_RULESET_ID)
                    throw new InvalidStateException("Attempted to select an unsupported ruleset.");

                ensureSettingsModsValid(settings);

                try
                {
                    room.Settings = settings;
                    await updateDatabaseSettings(room);
                }
                catch
                {
                    // rollback settings if an error occurred when updating the database.
                    room.Settings = previousSettings;
                    throw;
                }

                await ensureAllUsersValidMods(room);

                // this should probably only happen for gameplay-related changes, but let's just keep things simple for now.
                foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.Ready).ToArray())
                    await changeAndBroadcastUserState(room, u, MultiplayerUserState.Idle);

                await Clients.Group(GetGroupId(room.RoomID)).SettingsChanged(settings);
            }
        }

        /// <summary>
        /// Get the group ID to be used for multiplayer messaging.
        /// </summary>
        /// <param name="roomId">The databased room ID.</param>
        /// <param name="gameplay">Whether the group ID should be for active gameplay, or room control messages.</param>
        public static string GetGroupId(long roomId, bool gameplay = false) => $"room:{roomId}:{gameplay}";

        private async Task changeUserMods(IEnumerable<APIMod> newMods, MultiplayerRoom room, MultiplayerRoomUser user)
        {
            var newModList = newMods.ToList();

            if (!validateMods(room, newModList, out var validMods))
                throw new InvalidStateException($"Incompatible mods were selected: {string.Join(',', newModList.Except(validMods).Select(m => m.Acronym))}");

            if (user.Mods.SequenceEqual(newModList))
                return;

            user.Mods = newModList;

            await Clients.Group(GetGroupId(room.RoomID)).UserModsChanged(user.UserID, newModList);
        }

        private static void ensureSettingsModsValid(MultiplayerRoomSettings settings)
        {
            // check against ruleset
            if (!populateValidModsForRuleset(settings.RulesetID, settings.RequiredMods, out var requiredMods))
            {
                var invalidRequiredAcronyms = string.Join(',', settings.RequiredMods.Where(m => requiredMods.All(valid => valid.Acronym != m.Acronym)).Select(m => m.Acronym));
                throw new InvalidStateException($"Invalid mods were selected for specified ruleset: {invalidRequiredAcronyms}");
            }

            if (!populateValidModsForRuleset(settings.RulesetID, settings.AllowedMods, out var allowedMods))
            {
                var invalidAllowedAcronyms = string.Join(',', settings.AllowedMods.Where(m => allowedMods.All(valid => valid.Acronym != m.Acronym)).Select(m => m.Acronym));
                throw new InvalidStateException($"Invalid mods were selected for specified ruleset: {invalidAllowedAcronyms}");
            }

            if (!ModUtils.CheckCompatibleSet(requiredMods, out var invalid))
                throw new InvalidStateException($"Invalid combination of required mods: {string.Join(',', invalid.Select(m => m.Acronym))}");

            // check aggregate combinations with each allowed mod individually.
            foreach (var allowedMod in allowedMods)
            {
                if (!ModUtils.CheckCompatibleSet(requiredMods.Concat(new[] { allowedMod }), out invalid))
                    throw new InvalidStateException($"Invalid combination of required and allowed mods: {string.Join(',', invalid.Select(m => m.Acronym))}");
            }
        }

        private async Task ensureAllUsersValidMods(MultiplayerRoom room)
        {
            foreach (var user in room.Users)
            {
                if (!validateMods(room, user.Mods, out var validMods))
                    await changeUserMods(validMods, room, user);
            }
        }

        private static bool validateMods(MultiplayerRoom room, IEnumerable<APIMod> proposedMods, [NotNullWhen(false)] out IEnumerable<APIMod>? validMods)
        {
            bool proposedWereValid = true;

            proposedWereValid &= populateValidModsForRuleset(room.Settings.RulesetID, proposedMods, out var valid);

            // check allowed by room
            foreach (var mod in valid.ToList())
            {
                if (room.Settings.AllowedMods.All(m => m.Acronym != mod.Acronym))
                {
                    valid.Remove(mod);
                    proposedWereValid = false;
                }
            }

            // check valid as combination
            if (!ModUtils.CheckCompatibleSet(valid, out var invalid))
            {
                proposedWereValid = false;
                foreach (var mod in invalid)
                    valid.Remove(mod);
            }

            validMods = valid.Select(m => new APIMod(m));
            return proposedWereValid;
        }

        /// <summary>
        /// Verifies all proposed mods are valid for the room's ruleset, returning instantiated <see cref="Mod"/>s for further processing.
        /// </summary>
        /// <param name="rulesetID">The legacy ruleset ID to check against.</param>
        /// <param name="proposedMods">The proposed mods.</param>
        /// <param name="valid">A list of valid deserialised mods.</param>
        /// <returns>Whether all <see cref="proposedMods"/> were valid.</returns>
        private static bool populateValidModsForRuleset(int rulesetID, IEnumerable<APIMod> proposedMods, out List<Mod> valid)
        {
            valid = new List<Mod>();
            bool proposedWereValid = true;

            var ruleset = LegacyHelper.GetRulesetFromLegacyID(rulesetID);

            foreach (var apiMod in proposedMods)
            {
                try
                {
                    // will throw if invalid
                    valid.Add(apiMod.ToMod(ruleset));
                }
                catch
                {
                    proposedWereValid = false;
                }
            }

            return proposedWereValid;
        }

        private async Task selectNextPlaylistItem(MultiplayerRoom room)
        {
            long newPlaylistItemId;

            using (var db = databaseFactory.GetInstance())
            {
                // Expire the current playlist item.
                var currentItem = await db.GetCurrentPlaylistItemAsync(room.RoomID);
                await db.ExpirePlaylistItemAsync(currentItem.id);

                // Todo: Host-rotate matches will require different logic here.
                newPlaylistItemId = await db.AddPlaylistItemAsync(currentItem);
            }

            // Distribute the new playlist item ID to clients. All future playlist changes will affect this new one.
            room.Settings.PlaylistItemId = newPlaylistItemId;
            await Clients.Group(GetGroupId(room.RoomID)).SettingsChanged(room.Settings);
        }

        private async Task updateDatabaseSettings(MultiplayerRoom room)
        {
            using (var db = databaseFactory.GetInstance())
            {
                var item = new multiplayer_playlist_item(room);
                var dbItem = await db.GetPlaylistItemFromRoomAsync(room.RoomID, item.id);

                if (dbItem == null)
                    throw new InvalidStateException("Attempted to select a playlist item not contained by the room.");

                if (dbItem.expired)
                    throw new InvalidStateException("Attempted to select an expired playlist item.");

                string? beatmapChecksum = await db.GetBeatmapChecksumAsync(item.beatmap_id);

                if (beatmapChecksum == null)
                    throw new InvalidStateException("Attempted to select a beatmap which does not exist online.");

                if (room.Settings.BeatmapChecksum != beatmapChecksum)
                    throw new InvalidStateException("Attempted to select a beatmap which has been modified.");

                await db.UpdateRoomSettingsAsync(room);
            }
        }

        private async Task updateDatabaseHost(MultiplayerRoom room)
        {
            using (var db = databaseFactory.GetInstance())
                await db.UpdateRoomHostAsync(room);
        }

        private async Task endDatabaseMatch(MultiplayerRoom room)
        {
            using (var db = databaseFactory.GetInstance())
                await db.EndMatchAsync(room);
        }

        private async Task addDatabaseUser(MultiplayerRoom room, MultiplayerRoomUser user)
        {
            using (var db = databaseFactory.GetInstance())
                await db.AddRoomParticipantAsync(room, user);
        }

        private async Task removeDatabaseUser(MultiplayerRoom room, MultiplayerRoomUser user)
        {
            using (var db = databaseFactory.GetInstance())
                await db.RemoveRoomParticipantAsync(room, user);
        }

        protected override async Task CleanUpState(MultiplayerClientState state)
        {
            await leaveRoom(state);
            await base.CleanUpState(state);
        }

        private async Task setNewHost(MultiplayerRoom room, MultiplayerRoomUser newHost)
        {
            room.Host = newHost;
            await Clients.Group(GetGroupId(room.RoomID)).HostChanged(newHost.UserID);

            await updateDatabaseHost(room);
        }

        /// <summary>
        /// Should be called when user states change, to check whether the new overall room state can trigger a room-level state change.
        /// </summary>
        private async Task updateRoomStateIfRequired(MultiplayerRoom room)
        {
            //check whether a room state change is required.
            switch (room.State)
            {
                case MultiplayerRoomState.WaitingForLoad:
                    if (room.Users.All(u => u.State != MultiplayerUserState.WaitingForLoad))
                    {
                        var loadedUsers = room.Users.Where(u => u.State == MultiplayerUserState.Loaded).ToArray();

                        if (loadedUsers.Length == 0)
                        {
                            // all users have bailed from the load sequence. cancel the game start.
                            await changeRoomState(room, MultiplayerRoomState.Open);
                            return;
                        }

                        foreach (var u in loadedUsers)
                            await changeAndBroadcastUserState(room, u, MultiplayerUserState.Playing);

                        await Clients.Group(GetGroupId(room.RoomID)).MatchStarted();

                        await changeRoomState(room, MultiplayerRoomState.Playing);
                    }

                    break;

                case MultiplayerRoomState.Playing:
                    if (room.Users.All(u => u.State != MultiplayerUserState.Playing))
                    {
                        foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.FinishedPlay))
                            await changeAndBroadcastUserState(room, u, MultiplayerUserState.Results);

                        await changeRoomState(room, MultiplayerRoomState.Open);
                        await Clients.Group(GetGroupId(room.RoomID)).ResultsReady();

                        await selectNextPlaylistItem(room);
                    }

                    break;
            }
        }

        private Task changeAndBroadcastUserState(MultiplayerRoom room, MultiplayerRoomUser user, MultiplayerUserState state)
        {
            user.State = state;
            return Clients.Group(GetGroupId(room.RoomID)).UserStateChanged(user.UserID, user.State);
        }

        /// <summary>
        /// Changes the provided room's state and notifies all users.
        /// </summary>
        private async Task changeRoomState(MultiplayerRoom room, MultiplayerRoomState newState)
        {
            room.State = newState;
            await Clients.Group(GetGroupId(room.RoomID)).RoomStateChanged(newState);
        }

        /// <summary>
        /// Given a room and a state transition, throw if there's an issue with the sequence of events.
        /// </summary>
        /// <param name="room">The room.</param>
        /// <param name="oldState">The old state.</param>
        /// <param name="newState">The new state.</param>
        private void ensureValidStateSwitch(MultiplayerRoom room, MultiplayerUserState oldState, MultiplayerUserState newState)
        {
            switch (newState)
            {
                case MultiplayerUserState.Idle:
                    // any state can return to idle.
                    break;

                case MultiplayerUserState.Ready:
                    if (oldState != MultiplayerUserState.Idle)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.WaitingForLoad:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.Loaded:
                    if (oldState != MultiplayerUserState.WaitingForLoad)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.Playing:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.FinishedPlay:
                    if (oldState != MultiplayerUserState.Playing)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.Results:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.Spectating:
                    if (oldState != MultiplayerUserState.Idle && oldState != MultiplayerUserState.Ready)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(newState), newState, null);
            }
        }

        /// <summary>
        /// Ensure the local user is the host of the room, and throw if they are not.
        /// </summary>
        private void ensureIsHost(MultiplayerRoom room)
        {
            if (room.Host?.UserID != CurrentContextUserId)
                throw new NotHostException();
        }

        /// <summary>
        /// Retrieve the <see cref="MultiplayerRoom"/> for the local context user.
        /// </summary>
        private async Task<ItemUsage<ServerMultiplayerRoom>> getLocalUserRoom(MultiplayerClientState? state)
        {
            if (state == null)
                throw new NotJoinedRoomException();

            long roomId = state.CurrentRoomID;

            return await Rooms.GetForUse(roomId);
        }

        private async Task leaveRoom(MultiplayerClientState state)
        {
            using (var roomUsage = await getLocalUserRoom(state))
                await leaveRoom(state, roomUsage);
        }

        private async Task leaveRoom(MultiplayerClientState state, ItemUsage<ServerMultiplayerRoom> roomUsage)
        {
            var room = roomUsage.Item;

            if (room == null)
                throw new InvalidOperationException("Attempted to operate on a null room");

            Log($"Leaving room {room.RoomID}");

            await Groups.RemoveFromGroupAsync(state.ConnectionId, GetGroupId(room.RoomID, true));
            await Groups.RemoveFromGroupAsync(state.ConnectionId, GetGroupId(room.RoomID));

            var user = room.Users.FirstOrDefault(u => u.UserID == state.UserId);

            if (user == null)
                throw new InvalidStateException("User was not in the expected room.");

            room.Users.Remove(user);
            await removeDatabaseUser(room, user);

            // handle closing the room if the only participant is the user which is leaving.
            if (room.Users.Count == 0)
            {
                await endDatabaseMatch(room);

                // only destroy the usage after the database operation succeeds.
                Log($"Stopping tracking of room {room.RoomID} (all users left).");
                roomUsage.Destroy();
                return;
            }

            await updateRoomStateIfRequired(room);

            var clients = Clients.Group(GetGroupId(room.RoomID));

            // if this user was the host, we need to arbitrarily transfer host so the room can continue to exist.
            if (room.Host?.Equals(user) == true)
            {
                // there *has* to still be at least one user in the room (see user check above).
                var newHost = room.Users.First();

                await setNewHost(room, newHost);
            }

            await clients.UserLeft(user);
        }

        private static MatchTypeImplementation getTypeImplementation(MultiplayerRoomSettings settings, ServerMultiplayerRoom room)
        {
            MatchTypeImplementation typeImplementation;

            switch (settings.MatchType)
            {
                case MatchType.TeamVersus:
                    typeImplementation = new TeamVersus(room);
                    break;

                default:
                    typeImplementation = new HeadToHead(room);
                    break;
            }

            return typeImplementation;
        }

        public Task SendMatchEvent(MultiplayerRoom room, MatchServerEvent e)
        {
            return Clients.Group(GetGroupId(room.RoomID)).MatchEvent(e);
        }

        public Task UpdateMatchRoomState(MultiplayerRoom room)
        {
            return Clients.Group(GetGroupId(room.RoomID)).MatchRoomStateChanged(room.MatchState);
        }

        public Task UpdateMatchUserState(MultiplayerRoom room, MultiplayerRoomUser user)
        {
            return Clients.Group(GetGroupId(room.RoomID)).MatchUserStateChanged(user.UserID, user.MatchState);
        }
    }
}
