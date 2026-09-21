using Course.Shared;
using Npgsql;
using Xunit;

namespace Course.Tests;

/// <summary>Отказ хранилища классифицируется по SQLSTATE: транзиентное — повторяемо, прочее — нет.</summary>
public sealed class PostgresFailureTests
{
    [Theory]
    [InlineData("40P01")]
    [InlineData("40001")]
    public void Lock_and_serialization_conflicts_are_retryable_dependency_failures(string sqlState)
    {
        var exception = Postgres(sqlState);

        Assert.True(PostgresFailure.IsTransientConflict(exception));
        Assert.Equal(ContractCodes.DependencyUnavailable, PostgresFailure.Classify(exception));
        Assert.Equal(503, ContractCodes.HttpStatus(PostgresFailure.Classify(exception)));
        // Проигравшая транзакция — не отказ базы: сервис не должен помечать зависимость недоступной.
        Assert.False(PostgresFailure.IsDependencyOutage(exception));
    }

    [Theory]
    [InlineData("08006")]
    [InlineData("57P01")]
    [InlineData("53300")]
    public void Connection_and_server_failures_are_outages(string sqlState)
        => Assert.True(PostgresFailure.IsDependencyOutage(Postgres(sqlState)));

    [Theory]
    [InlineData("57014", ContractCodes.ActionTimeout)]
    [InlineData("08006", ContractCodes.DependencyUnavailable)]
    [InlineData("23505", ContractCodes.InternalError)]
    [InlineData("P0001", ContractCodes.InternalError)]
    public void Other_states_keep_their_classification(string sqlState, string expected)
    {
        var exception = Postgres(sqlState);

        Assert.False(PostgresFailure.IsTransientConflict(exception));
        Assert.Equal(expected, PostgresFailure.Classify(exception));
    }

    [Fact]
    public void Conflict_is_found_behind_a_wrapping_exception()
        => Assert.True(PostgresFailure.IsTransientConflict(new InvalidOperationException("wrapped", Postgres("40P01"))));

    private static PostgresException Postgres(string sqlState)
        => new("synthetic failure", "ERROR", "ERROR", sqlState);
}
