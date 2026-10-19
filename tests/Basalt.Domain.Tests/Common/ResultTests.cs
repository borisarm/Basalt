using Basalt.Domain.Common;

namespace Basalt.Domain.Tests.Common;

public sealed class ResultTests
{
    private static readonly Error SampleError = new("test.failure", "Something failed");

    [Fact]
    public void Success_ExposesValue()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Success_ErrorAccess_Throws()
    {
        var result = Result<int>.Success(42);

        var act = () => result.Error;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_ExposesError()
    {
        var result = Result<int>.Failure(SampleError);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SampleError);
    }

    [Fact]
    public void Failure_ValueAccess_Throws()
    {
        var result = Result<int>.Failure(SampleError);

        var act = () => result.Value;

        act.Should().Throw<InvalidOperationException>().WithMessage("*test.failure*");
    }

    [Fact]
    public void Failure_NullError_Throws()
    {
        var act = () => Result<int>.Failure(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Default_IsFailureWithUninitializedError()
    {
        var result = default(Result<int>);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(DomainErrors.ResultUninitialized);
    }

    [Fact]
    public void Map_OnSuccess_TransformsValue()
    {
        var result = Result<int>.Success(2).Map(x => x * 10);

        result.Value.Should().Be(20);
    }

    [Fact]
    public void Map_OnFailure_PropagatesErrorWithoutInvokingFunction()
    {
        var invoked = false;

        var result = Result<int>.Failure(SampleError).Map(x =>
        {
            invoked = true;
            return x;
        });

        invoked.Should().BeFalse();
        result.Error.Should().Be(SampleError);
    }

    [Fact]
    public void Bind_OnSuccess_ChainsResult()
    {
        var success = Result<int>.Success(2).Bind(x => Result<string>.Success($"v{x}"));
        var failure = Result<int>.Success(2).Bind(_ => Result<string>.Failure(SampleError));

        success.Value.Should().Be("v2");
        failure.Error.Should().Be(SampleError);
    }

    [Fact]
    public void Bind_OnFailure_PropagatesError()
    {
        var result = Result<int>.Failure(SampleError).Bind(x => Result<string>.Success("never"));

        result.Error.Should().Be(SampleError);
    }

    [Fact]
    public async Task BindAsync_OnSuccess_ChainsResult()
    {
        var result = await Result<int>.Success(3).BindAsync(x => Task.FromResult(Result<int>.Success(x + 1)));

        result.Value.Should().Be(4);
    }

    [Fact]
    public async Task BindAsync_OnFailure_PropagatesError()
    {
        var invoked = false;

        var result = await Result<int>.Failure(SampleError).BindAsync(x =>
        {
            invoked = true;
            return Task.FromResult(Result<int>.Success(x));
        });

        invoked.Should().BeFalse();
        result.Error.Should().Be(SampleError);
    }

    [Fact]
    public void Match_SelectsBranch()
    {
        Result<int>.Success(1).Match(v => $"ok {v}", e => e.Code).Should().Be("ok 1");
        Result<int>.Failure(SampleError).Match(v => $"ok {v}", e => e.Code).Should().Be("test.failure");
    }

    [Fact]
    public void ImplicitConversions_CreateResults()
    {
        Result<int> fromValue = 5;
        Result<int> fromError = SampleError;

        fromValue.Value.Should().Be(5);
        fromError.Error.Should().Be(SampleError);
    }

    [Fact]
    public void NonGenericHelpers_CreateUnitResults()
    {
        Result.Success().IsSuccess.Should().BeTrue();
        Result.Success().Value.Should().Be(Unit.Value);
        Result.Failure(SampleError).Error.Should().Be(SampleError);
    }

    [Fact]
    public void GenericHelpers_CreateTypedResults()
    {
        Result.Success("x").Value.Should().Be("x");
        Result.Failure<string>(SampleError).Error.Should().Be(SampleError);
        Unit.Value.ToString().Should().Be("()");
    }

    [Fact]
    public void Error_WithTwice_KeepsPreviousMetadata()
    {
        var error = SampleError.With("a", 1).With("b", 2);

        error.Metadata.Should().HaveCount(2);
    }

    [Fact]
    public async Task Combinators_RejectNullFunctions()
    {
        var result = Result<int>.Success(1);

        FluentActions.Invoking(() => result.Map<int>(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => result.Bind<int>(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => result.Match<int>(null!, _ => 0)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => result.Match<int>(_ => 0, null!)).Should().Throw<ArgumentNullException>();
        await FluentActions.Awaiting(() => result.BindAsync<int>(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void ErrorMessages_DescribeMisuse()
    {
        FluentActions.Invoking(() => Result<int>.Success(1).Error).Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot access the error of a successful result.");
        default(Result<int>).Error.Message.Should().Be("Result was not initialized.");
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Result<int>.Success(1).Should().Be(Result<int>.Success(1));
        Result<int>.Success(1).Should().NotBe(Result<int>.Success(2));
        Result<int>.Failure(SampleError).Should().Be(Result<int>.Failure(SampleError));
    }

    [Fact]
    public void ToString_DescribesState()
    {
        Result<int>.Success(1).ToString().Should().Be("Success(1)");
        Result<int>.Failure(SampleError).ToString().Should().Be("Failure(test.failure: Something failed)");
    }

    [Fact]
    public void Error_WithMetadata_ExposesValues()
    {
        var error = SampleError.With("path", "a.md");

        error.Metadata.Should().ContainKey("path").WhoseValue.Should().Be("a.md");
        error.Code.Should().Be("test.failure");
        SampleError.Metadata.Should().BeNull();
    }

    [Fact]
    public void Error_ToString_IncludesCodeAndMessage()
    {
        SampleError.ToString().Should().Be("test.failure: Something failed");
    }
}
