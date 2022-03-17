// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Utils;

#nullable enable

namespace osu.Server.Spectator.Extensions
{
    public static class MultiplayerPlaylistItemExtensions
    {
        /// <summary>
        /// Checks whether the given mods are compatible with the current playlist item's mods and ruleset.
        /// </summary>
        /// <param name="item">The <see cref="MultiplayerPlaylistItem"/> to validate the user mods for.</param>
        /// <param name="proposedMods">The proposed user mods to check against the <see cref="MultiplayerPlaylistItem"/>.</param>
        /// <param name="validMods">The set of mods which _are_ valid.</param>
        /// <returns>Whether all user mods are valid for the <see cref="MultiplayerPlaylistItem"/>.</returns>
        public static bool ValidateUserMods(this MultiplayerPlaylistItem item, IEnumerable<APIMod> proposedMods, [NotNullWhen(false)] out IEnumerable<APIMod>? validMods)
        {
            var ruleset = LegacyHelper.GetRulesetFromLegacyID(item.RulesetID);

            bool proposedWereValid = true;
            ModUtils.InstantiateModsForRuleset(ruleset, proposedMods, out var valid);

            // check allowed by room
            foreach (var mod in valid.ToList())
            {
                if (item.AllowedMods.All(m => m.Acronym != mod.Acronym))
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
        /// Ensures that a <see cref="MultiplayerPlaylistItem"/>'s required and allowed mods are compatible with each other and the room's ruleset.
        /// </summary>
        /// <exception cref="InvalidStateException">If the mods are invalid.</exception>
        public static void EnsureModsValid(this MultiplayerPlaylistItem item)
        {
            var ruleset = LegacyHelper.GetRulesetFromLegacyID(item.RulesetID);

            ModUtils.InstantiateModsForRuleset(ruleset, item.RequiredMods, out var requiredMods);
            ModUtils.InstantiateModsForRuleset(ruleset, item.AllowedMods, out var allowedMods);

            if (!ModUtils.CheckCompatibleSet(requiredMods, out var invalid))
                throw new InvalidStateException($"Invalid combination of required mods: {string.Join(',', invalid.Select(m => m.Acronym))}");

            // check aggregate combinations with each allowed mod individually.
            foreach (var allowedMod in allowedMods)
            {
                if (!ModUtils.CheckCompatibleSet(requiredMods.Concat(new[] { allowedMod }), out invalid))
                    throw new InvalidStateException($"Invalid combination of required and allowed mods: {string.Join(',', invalid.Select(m => m.Acronym))}");
            }
        }
    }
}
