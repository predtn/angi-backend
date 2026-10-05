using System.Linq.Expressions;
using System.Text.Json;
using ANGI.Domain.Common;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
namespace ANGI.Infrastructure.Persistences.Conventions
{
    public static class ModelBuilderConventions
    {
        // Enum columns are varchar + CHECK (or smallint for FeedbackValue). The CHECK list is built
        // from the enum itself so the database can never drift from the code.
        public static void ApplyEnumConventions(this ModelBuilder modelBuilder)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName()!;

                foreach (var property in entityType.GetProperties())
                {
                    var enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                    if (!enumType.IsEnum)
                    {
                        continue;
                    }

                    var columnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
                    string allowedValues;

                    if (Enum.GetUnderlyingType(enumType) == typeof(short))
                    {
                        property.SetProviderClrType(typeof(short));
                        allowedValues = string.Join(", ", Enum.GetValues(enumType).Cast<object>().Select(value => Convert.ToInt16(value)));
                    }
                    else
                    {
                        var namingPolicy = enumType == typeof(OutboxEventType)
                            ? JsonNamingPolicy.SnakeCaseUpper
                            : JsonNamingPolicy.SnakeCaseLower;
                        var converterType = typeof(SnakeCaseEnumConverter<>).MakeGenericType(enumType);
                        var converter = (ValueConverter)Activator.CreateInstance(converterType, namingPolicy)!;
                        var dbValues = (IReadOnlyCollection<string>)converterType.GetProperty("DbValues")!.GetValue(converter)!;

                        property.SetValueConverter(converter);
                        allowedValues = string.Join(", ", dbValues.Select(value => $"'{value}'"));
                    }

                    entityType.AddCheckConstraint($"ck_{tableName}_{columnName}", $"{columnName} IN ({allowedValues})");
                }
            }
        }

        public static void ApplyTimestampConventions(this ModelBuilder modelBuilder)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (typeof(IHasCreatedAt).IsAssignableFrom(entityType.ClrType))
                {
                    entityType.FindProperty(nameof(IHasCreatedAt.CreatedAt))!.SetDefaultValueSql("now()");
                }

                if (typeof(IHasUpdatedAt).IsAssignableFrom(entityType.ClrType))
                {
                    entityType.FindProperty(nameof(IHasUpdatedAt.UpdatedAt))!.SetDefaultValueSql("now()");
                }
            }
        }

        public static void ApplySoftDeleteFilters(this ModelBuilder modelBuilder)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (!typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType))
                {
                    continue;
                }

                var entity = Expression.Parameter(entityType.ClrType, "e");
                var deletedAt = Expression.Property(entity, nameof(ISoftDelete.DeletedAt));
                var notDeleted = Expression.Equal(deletedAt, Expression.Constant(null, typeof(DateTime?)));
                entityType.SetQueryFilter(Expression.Lambda(notDeleted, entity));
            }
        }

        // A constant database default (e.g. dish_form = 'other', is_available = true) must not make
        // EF treat the CLR default (DishForm.Soup, false) as "unset". Entities set these defaults in C#
        // and EF always sends the value; the database default only serves raw SQL inserts.
        // Required FKs that are not configured explicitly become RESTRICT instead of CASCADE.
        public static void ApplyDefaultValueAndDeleteConventions(this ModelBuilder modelBuilder)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.FindAnnotation(RelationalAnnotationNames.DefaultValue) is not null)
                    {
                        property.ValueGenerated = ValueGenerated.Never;
                    }
                }

                foreach (var foreignKey in entityType.GetForeignKeys())
                {
                    if (foreignKey.DeleteBehavior == DeleteBehavior.ClientSetNull)
                    {
                        foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
                    }
                }
            }
        }
    }
}
