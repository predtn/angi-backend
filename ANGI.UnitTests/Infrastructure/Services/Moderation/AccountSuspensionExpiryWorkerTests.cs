using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Infrastructure.Services.Moderation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ANGI.UnitTests.Infrastructure.Services.Moderation;

/// <summary>Checks the expiry worker using a manually fired timer and isolated scoped use cases.</summary>
public sealed class AccountSuspensionExpiryWorkerTests
{
    // TEST-01: Poll once per minute and drain all nonempty batches before waiting for another tick.
    /// <summary>Verifies the scheduling interval, bounded batch loop and scoped use-case execution.</summary>
    [Fact]
    public async Task TickDrainsAllExpiredBatches()
    {
        var clock = new ManualClock();
        var useCase = new Mock<IExpireUserSuspensionsUseCase>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        useCase.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 3) done.TrySetResult();
            return Task.FromResult(call < 3 ? 100 : 0);
        });
        using var provider = new ServiceCollection().AddScoped(_ => useCase.Object).BuildServiceProvider();
        using var worker = new AccountSuspensionExpiryWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            Mock.Of<ILogger<AccountSuspensionExpiryWorker>>());
        await worker.StartAsync(default);
        await clock.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Period.Should().Be(TimeSpan.FromMinutes(1));
        calls.Should().Be(0);
        clock.Fire();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        calls.Should().Be(3);
    }

    // TEST-02: A failed expiry pass logs its failure and retries successfully on a later timer tick.
    /// <summary>Checks a database outage cannot terminate the background worker.</summary>
    [Fact]
    public async Task FailureRetriesOnNextTick()
    {
        var clock = new ManualClock();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Mock<ILogger<AccountSuspensionExpiryWorker>>();
        var useCase = new Mock<IExpireUserSuspensionsUseCase>();
        var calls = 0;
        useCase.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                first.TrySetResult();
                return Task.FromException<int>(new InvalidOperationException("database unavailable"));
            }
            recovered.TrySetResult();
            return Task.FromResult(0);
        });
        using var provider = new ServiceCollection().AddScoped(_ => useCase.Object).BuildServiceProvider();
        using var worker = new AccountSuspensionExpiryWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock, logger.Object);
        await worker.StartAsync(default);
        await clock.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        logger.Invocations.Should().ContainSingle(x => x.Method.Name == "Log");
    }

    // TEST-03: Shutdown while waiting for a tick completes cleanly without calling the expiry use case.
    /// <summary>Checks host cancellation does not produce errors or stray database operations.</summary>
    [Fact]
    public async Task StopCancelsPendingTick()
    {
        var useCase = new Mock<IExpireUserSuspensionsUseCase>();
        using var provider = new ServiceCollection().AddScoped(_ => useCase.Object).BuildServiceProvider();
        var clock = new ManualClock();
        using var worker = new AccountSuspensionExpiryWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            Mock.Of<ILogger<AccountSuspensionExpiryWorker>>());
        await worker.StartAsync(default);
        await clock.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        useCase.VerifyNoOtherCalls();
    }

    /// <summary>Captures the worker's periodic callback so tests can advance scheduling without real sleeps.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal TimeSpan Period { get; private set; }
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Captures the periodic callback without creating an operating-system timer.</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            Period = period;
            Ready.TrySetResult();
            return new ManualTimer();
        }

        /// <summary>Releases one pending periodic wait.</summary>
        internal void Fire() => _callback!(_state);
    }

    /// <summary>Provides the timer lifetime required by PeriodicTimer without scheduling real callbacks.</summary>
    private sealed class ManualTimer : ITimer
    {
        /// <summary>Accepts schedule changes for the manually controlled timer.</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        /// <summary>Releases the timer, which has no external resources.</summary>
        public void Dispose() { }
        /// <summary>Completes asynchronous timer disposal immediately.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
