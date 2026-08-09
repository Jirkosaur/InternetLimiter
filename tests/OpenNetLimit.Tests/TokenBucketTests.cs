using OpenNetLimit.Engine.RateLimiting;
using Xunit;

namespace OpenNetLimit.Tests;

public class TokenBucketTests
{
    [Fact]
    public void NewBucket_HasFullCapacity()
    {
        var bucket = new TokenBucket(1000);
        Assert.True(bucket.AvailableTokens >= 1000);
    }

    [Fact]
    public void TryConsume_WithinCapacity_ReturnsTrue()
    {
        var bucket = new TokenBucket(100_000);
        Assert.True(bucket.TryConsume(1000));
    }

    [Fact]
    public void TryConsume_ExceedsCapacity_ReturnsFalse()
    {
        var bucket = new TokenBucket(100);
        bucket.TryConsume(65536);
        Assert.False(bucket.TryConsume(65536));
    }

    [Fact]
    public void GetDelay_WhenTokensAvailable_ReturnsZero()
    {
        var bucket = new TokenBucket(100_000);
        Assert.Equal(TimeSpan.Zero, bucket.GetDelay(1000));
    }

    [Fact]
    public void GetDelay_WhenTokensDepleted_ReturnsPositiveDelay()
    {
        var bucket = new TokenBucket(1000);
        bucket.TryConsume(65536);
        var delay = bucket.GetDelay(1000);
        Assert.True(delay > TimeSpan.Zero);
    }

    [Fact]
    public void Capacity_LowLimit_UsesSmallFloor()
    {
        var bucket = new TokenBucket(1024);
        Assert.Equal(8192, bucket.CapacityBytes);
    }

    [Fact]
    public void Capacity_LargeLimit_EqualsLimit()
    {
        var bucket = new TokenBucket(1_000_000);
        Assert.Equal(1_000_000, bucket.CapacityBytes);
    }

    [Fact]
    public void TokensRefill_AfterWait()
    {
        var bucket = new TokenBucket(1_000_000);
        bucket.TryConsume((int)bucket.CapacityBytes);

        Thread.Sleep(50);

        Assert.True(bucket.AvailableTokens > 0);
    }

    [Fact]
    public void TryConsume_WhenInsufficient_CarriesDeficit()
    {
        var bucket = new TokenBucket(1_000_000);
        bucket.TryConsume((int)bucket.CapacityBytes);
        double before = bucket.AvailableTokens;
        bucket.TryConsume(100_000);
        double after = bucket.AvailableTokens;
        Assert.True(after < before - 50_000);
    }

    [Fact]
    public void GetDelay_ReflectsCumulativeDeficit()
    {
        var bucket = new TokenBucket(1000);
        bucket.TryConsume((int)bucket.CapacityBytes);
        bucket.TryConsume(1000);
        bucket.TryConsume(1000);
        var delay = bucket.GetDelay(1000);
        Assert.True(delay > TimeSpan.FromSeconds(2.9));
    }
}

public class ProcessRateLimiterTests
{
    [Fact]
    public void NoLimit_TryConsume_ReturnsTrue()
    {
        var limiter = new ProcessRateLimiter();
        Assert.True(limiter.TryConsume(1234, 5000, false));
    }

    [Fact]
    public void WithLimit_TryConsume_EnforcesLimit()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 1000);

        limiter.TryConsume(1, 65536, false);
        Assert.False(limiter.TryConsume(1, 65536, false));
    }

    [Fact]
    public void HasLimit_ReturnsCorrectState()
    {
        var limiter = new ProcessRateLimiter();
        Assert.False(limiter.HasLimit(1));

        limiter.SetLimit(1, 1000, 1000);
        Assert.True(limiter.HasLimit(1));

        limiter.RemoveLimit(1);
        Assert.False(limiter.HasLimit(1));
    }

    [Fact]
    public void RemoveAll_ClearsAllLimits()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 1000);
        limiter.SetLimit(2, 2000, 2000);

        limiter.RemoveAll();

        Assert.False(limiter.HasLimit(1));
        Assert.False(limiter.HasLimit(2));
    }

    [Fact]
    public void GetDelay_NoLimit_ReturnsZero()
    {
        var limiter = new ProcessRateLimiter();
        Assert.Equal(TimeSpan.Zero, limiter.GetDelay(999, 5000, false));
    }

    [Fact]
    public void SeparateUploadDownloadLimits()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 500);

        limiter.TryConsume(1, 65536, false);
        var downloadDelay = limiter.GetDelay(1, 1000, false);

        limiter.TryConsume(1, 65536, true);
        var uploadDelay = limiter.GetDelay(1, 1000, true);

        Assert.True(downloadDelay > TimeSpan.Zero);
        Assert.True(uploadDelay > TimeSpan.Zero);
    }

    [Fact]
    public void SetLimit_SameLimits_PreservesBucketState()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 1000);

        limiter.TryConsume(1, 65536, false);
        Assert.True(limiter.GetDelay(1, 1000, false) > TimeSpan.Zero);

        limiter.SetLimit(1, 1000, 1000);

        Assert.True(limiter.GetDelay(1, 1000, false) > TimeSpan.Zero);
    }

    [Fact]
    public void SetLimit_ChangedLimit_ReplacesBucket()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 1000);

        limiter.TryConsume(1, 65536, false);
        limiter.SetLimit(1, 2000, 1000);

        Assert.Equal(TimeSpan.Zero, limiter.GetDelay(1, 1000, false));
    }

    [Fact]
    public void SetLimit_CalledRepeatedly_DoesNotResetBucket()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 1000);

        for (int i = 0; i < 100; i++)
            limiter.SetLimit(1, 1000, 1000);

        limiter.TryConsume(1, 65536, false);

        for (int i = 0; i < 100; i++)
            limiter.SetLimit(1, 1000, 1000);

        Assert.True(limiter.GetDelay(1, 1000, false) > TimeSpan.Zero);
    }
}
