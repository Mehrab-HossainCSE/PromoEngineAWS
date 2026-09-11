using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PromoEngine.Infrastructure.Postgres;

/// <summary>
/// The two places PostgreSQL does not behave the way the SQL Server schema did, kept
/// in one file so both contexts get the same treatment and neither can drift.
/// </summary>
public static class PostgresConventions
{
    /// <summary>
    /// Name of the case-insensitive collation created in every database.
    ///
    /// SQL Server ran on its default <c>SQL_Latin1_General_CP1_CI_AS</c> collation, so
    /// every string comparison the application made was case-insensitive without
    /// asking. PostgreSQL compares text case-sensitively, which would silently change
    /// results: a till sending <c>6156ba16419876</c> matched an uploaded
    /// <c>6156BA16419876</c> before and would stop matching now.
    ///
    /// Rather than rewrite the queries - the application logic is not supposed to
    /// change - the columns that were being compared case-insensitively are given a
    /// non-deterministic ICU collation, which restores exactly the old semantics for
    /// equality, IN, DISTINCT, GROUP BY and unique indexes.
    /// </summary>
    public const string CaseInsensitiveCollation = "case_insensitive";

    private const string CaseInsensitiveLocale = "und-u-ks-level2";

    /// <summary>
    /// Declares the collation on the model so that EnsureCreated emits
    /// <c>CREATE COLLATION</c> alongside the tables. Requires a PostgreSQL built with
    /// ICU support, which every mainstream distribution of 13 and later is.
    ///
    /// The collation is deliberately non-deterministic: that is what makes 'A' and 'a'
    /// compare equal rather than merely sort together. The trade-off is that
    /// PostgreSQL refuses LIKE against a non-deterministic column, so it is applied
    /// only to columns compared for equality - never to the description columns the
    /// promotion search does a Contains on.
    /// </summary>
    public static void AddCaseInsensitiveCollation(this ModelBuilder b) =>
        b.HasCollation(CaseInsensitiveCollation, locale: CaseInsensitiveLocale, provider: "icu", deterministic: false);

    /// <summary>
    /// Normalises every DateTimeOffset to UTC on the way to the database.
    ///
    /// Npgsql maps DateTimeOffset to <c>timestamptz</c> and throws outright on any
    /// value whose offset is not zero, so a client posting an offer starting
    /// <c>2026-01-01T00:00:00+06:00</c> would fail where SQL Server accepted it. The
    /// converter keeps that request working by storing the same instant in UTC.
    ///
    /// This is a storage change, not a behavioural one: <c>timestamptz</c> records an
    /// instant and not the writer's offset, and every comparison in the application -
    /// date windows, lead time, ordering - is an instant comparison. What is lost is
    /// the original offset, which nothing reads back.
    /// </summary>
    public static void UseUtcDateTimeOffsets(this ModelConfigurationBuilder b)
    {
        b.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    private sealed class UtcDateTimeOffsetConverter()
        : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);
}
