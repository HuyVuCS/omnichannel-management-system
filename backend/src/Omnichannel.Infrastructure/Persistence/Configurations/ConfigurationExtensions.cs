using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal static class ConfigurationExtensions
{
    public const int ActorMaxLength = 100;
    public const int EnumMaxLength = 32;
    public const int CorrelationIdMaxLength = 100;
    public const int MoneyPrecision = 18;
    public const int MoneyScale = 2;

    public static void ConfigureAuditColumns<T>(this EntityTypeBuilder<T> builder)
        where T : AuditableEntity
    {
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(ActorMaxLength);
        builder.Property(x => x.UpdatedBy).HasMaxLength(ActorMaxLength);
    }

    /// <summary>Stores enums as readable strings so values survive enum reordering.</summary>
    public static PropertyBuilder<TEnum> HasEnumAsString<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum
    {
        return property.HasConversion<string>().HasMaxLength(EnumMaxLength).IsRequired();
    }
}
