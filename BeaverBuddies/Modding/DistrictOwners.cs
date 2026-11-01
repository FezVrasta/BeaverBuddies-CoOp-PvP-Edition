using System;
using Timberborn.GameDistricts;

namespace BeaverBuddies.Modding
{
    /**
     * Who owns a district, for the buildings that trade between districts
     * (Trading Post, Power Exchange, toll stations). Nobody does unless a
     * mod says so through the DistrictOwner hook (see ModBridge): then only a
     * district's owner sets up its side.
     */
    public static class DistrictOwners
    {
        internal static Func<DistrictCenter, string> OwnerOf;
        internal static Func<string, string> NameOf;

        public static string Of(DistrictCenter district) => district == null ? null : OwnerOf?.Invoke(district);

        public static string PlayerName(string playerID) => playerID == null ? null : NameOf?.Invoke(playerID) ?? playerID;
    }

    /**
     * An event that checks for itself who may send it. Mods that filter
     * events by the owner of the building they name should let these
     * through (they can match the interface by name).
     */
    public interface IOwnershipChecked
    {
    }
}
