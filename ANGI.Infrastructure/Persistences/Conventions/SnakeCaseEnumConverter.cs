using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ANGI.Infrastructure.Persistences.Conventions
{
    // Stores an enum as the snake_case string listed in sheet "Enum" of the API Design,
    // e.g. UserStatus.PendingVerification <-> "pending_verification".
    public sealed class SnakeCaseEnumConverter<TEnum> : ValueConverter<TEnum, string>
        where TEnum : struct, Enum
    {
        public IReadOnlyCollection<string> DbValues { get; }

        public SnakeCaseEnumConverter(JsonNamingPolicy namingPolicy)
            : this(BuildMaps(namingPolicy))
        {
        }

        private SnakeCaseEnumConverter((Dictionary<TEnum, string> ToDb, Dictionary<string, TEnum> FromDb) maps)
            : base(value => maps.ToDb[value], value => maps.FromDb[value])
        {
            DbValues = maps.ToDb.Values.ToArray();
        }

        private static (Dictionary<TEnum, string>, Dictionary<string, TEnum>) BuildMaps(JsonNamingPolicy namingPolicy)
        {
            var toDb = Enum.GetValues<TEnum>().ToDictionary(value => value, value => namingPolicy.ConvertName(value.ToString()));
            var fromDb = toDb.ToDictionary(pair => pair.Value, pair => pair.Key);
            return (toDb, fromDb);
        }
    }
}
