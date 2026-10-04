using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Names every table, column, key, foreign key, and index of the model in snake_case. It is added in
/// the context's own conventions, so EF Core's <c>__EFMigrationsHistory</c> table, which is modelled
/// separately, keeps its default name and columns.
/// </summary>
internal sealed class SnakeCaseNamingConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entityTypes = modelBuilder.Metadata.GetEntityTypes().ToList();

        // Tables and columns first: the default names of keys and indexes are built from them.
        foreach (var entityType in entityTypes)
        {
            if (entityType.GetTableName() is { } table)
            {
                entityType.SetTableName(ToSnakeCase(table));
            }

            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }
        }

        foreach (var entityType in entityTypes)
        {
            foreach (var key in entityType.GetKeys())
            {
                if (key.GetName() is { } name)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                if (foreignKey.GetConstraintName() is { } name)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entityType.GetIndexes())
            {
                if (index.GetDatabaseName() is { } name)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    /// <summary>
    /// <c>LastAppliedMigrationId</c> becomes <c>last_applied_migration_id</c>; a run of capitals is one
    /// word (<c>SunoURLPath</c> becomes <c>suno_url_path</c>); existing underscores are kept.
    /// </summary>
    internal static string ToSnakeCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var result = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (char.IsUpper(current) && index > 0 && name[index - 1] != '_')
            {
                var afterLowerOrDigit = !char.IsUpper(name[index - 1]);
                var startsWordAfterCapitals = index + 1 < name.Length && char.IsLower(name[index + 1]);
                if (afterLowerOrDigit || startsWordAfterCapitals)
                {
                    result.Append('_');
                }
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }
}
